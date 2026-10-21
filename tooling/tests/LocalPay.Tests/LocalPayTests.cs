using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace EventTicketing.Tooling.LocalPay.Tests;

/// <summary>M1a L1: LocalPay behaves like a hosted-checkout provider, and misbehaves on demand.</summary>
public sealed class LocalPayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Approving_redirects_back_and_sends_a_signed_webhook()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2500);
        payment.Status.Should().Be("requires_payment");
        (await localPay.Browser.GetStringAsync(new Uri(payment.CheckoutUrl), Ct)).Should().Contain("Approve").And.Contain("25.00 EUR");

        var approved = await localPay.ApproveAsync(payment.Id);

        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        approved.Headers.Location.Should().Be(new Uri("http://shop.test/orders/1"));
        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("succeeded");

        var webhook = (await localPay.Shop.WaitForAsync(1)).Should().ContainSingle().Subject;
        webhook.Type.Should().Be("payment.succeeded");
        webhook.Data.GetProperty("paymentId").GetString().Should().Be(payment.Id);
        webhook.Data.GetProperty("amountMinorUnits").GetInt64().Should().Be(2500);
        IsSignedCorrectly(webhook).Should().BeTrue();
    }

    [Fact]
    public async Task Checkout_links_use_the_public_address_when_one_is_set()
    {
        // In the lab cluster the shop calls LocalPay by an internal name; the fan's browser can't.
        await using var localPay = new LocalPayUnderTest(publicUrl: "http://localhost:5201/");

        var payment = await localPay.CreateAsync(2500);

        payment.CheckoutUrl.Should().StartWith("http://localhost:5201/checkout/");
    }

    [Fact]
    public async Task Declining_fails_the_payment_and_says_so()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2500);

        (await localPay.DeclineAsync(payment.Id)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("failed");
        (await localPay.Shop.WaitForAsync(1)).Should().ContainSingle().Which.Type.Should().Be("payment.failed");
    }

    [Fact]
    public async Task The_fan_can_go_back_to_the_shop_without_paying_and_the_payment_stays_open()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2500);

        var page = await localPay.Browser.GetStringAsync(new Uri(payment.CheckoutUrl), Ct);

        page.Should().Contain("""<a class="button" href="http://shop.test/orders/1">Back to Test Shop</a>""");
        page.Should().Contain("the payment stays open until it expires");
        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("requires_payment");
        localPay.Shop.Received.Should().BeEmpty("going back is only a link: nothing is decided");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/orders/1")]
    public async Task Only_a_web_address_becomes_the_link_back(string returnUrl)
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2500, returnUrl: returnUrl);

        var page = await localPay.Browser.GetStringAsync(new Uri(payment.CheckoutUrl), Ct);

        page.Should().NotContain("Back to").And.Contain("Approve");
    }

    [Fact]
    public async Task A_repeated_idempotency_key_returns_the_same_payment()
    {
        await using var localPay = new LocalPayUnderTest();

        var first = await localPay.CreateAsync(2500, idempotencyKey: "order-42");
        var again = await localPay.CreateAsync(2500, idempotencyKey: "order-42");

        again.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task A_decided_payment_stays_decided()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2500);

        await localPay.ApproveAsync(payment.Id);
        await localPay.DeclineAsync(payment.Id);

        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("succeeded");
        (await localPay.Shop.WaitForAsync(1)).Should().ContainSingle("only the first decision is reported");
    }

    [Fact]
    public async Task An_open_payment_can_be_expired_early_and_then_not_paid()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2500);

        var expired = await localPay.ExpireAsync(payment.Id);
        expired.StatusCode.Should().Be(HttpStatusCode.OK);
        (await localPay.ExpireAsync(payment.Id)).StatusCode.Should().Be(HttpStatusCode.OK, "repeating it is fine");

        await localPay.ApproveAsync(payment.Id);
        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("expired");
        (await localPay.Shop.WaitForAsync(0)).Should().BeEmpty();
    }

    [Fact]
    public async Task Expiring_a_completed_payment_is_refused_with_its_status()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2500);
        await localPay.ApproveAsync(payment.Id);

        var expire = await localPay.ExpireAsync(payment.Id);

        expire.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await expire.Content.ReadFromJsonAsync<PaymentResponse>(Ct))!.Status.Should().Be("succeeded");
    }

    [Fact]
    public async Task A_payment_past_its_expiry_can_no_longer_be_paid()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2500, expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(200));

        await Task.Delay(300, Ct);
        await localPay.ApproveAsync(payment.Id);

        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("expired");
        (await localPay.Browser.GetStringAsync(new Uri(payment.CheckoutUrl), Ct)).Should().Contain("expired").And.NotContain("Approve");
    }

    [Fact]
    public async Task A_refund_returns_the_money_once_per_idempotency_key_and_never_more_than_was_paid()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(5000);
        await localPay.ApproveAsync(payment.Id);
        await localPay.Shop.WaitForAsync(1);

        var refund = await RefundAsync(localPay, payment.Id, "refund-1");
        refund.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await refund.Content.ReadFromJsonAsync<RefundResponse>(Ct))!;
        body.AmountMinorUnits.Should().Be(5000);

        var repeated = await RefundAsync(localPay, payment.Id, "refund-1");
        repeated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await repeated.Content.ReadFromJsonAsync<RefundResponse>(Ct))!.Id.Should().Be(body.Id);

        (await RefundAsync(localPay, payment.Id, "refund-2")).StatusCode.Should().Be(HttpStatusCode.Conflict, "nothing is left to refund");
        (await localPay.StatusAsync(payment.Id)).RefundedMinorUnits.Should().Be(5000);

        var webhooks = await localPay.Shop.WaitForAsync(2);
        webhooks.Should().HaveCount(2);
        webhooks[1].Type.Should().Be("refund.succeeded");
        webhooks[1].Data.GetProperty("refundId").GetString().Should().Be(body.Id);
    }

    [Fact]
    public async Task Only_a_succeeded_payment_can_be_refunded()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(5000);

        (await RefundAsync(localPay, payment.Id, null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_webhook_is_retried_until_the_shop_accepts_it()
    {
        await using var localPay = new LocalPayUnderTest();
        localPay.Shop.AnswerNext(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable);
        var payment = await localPay.CreateAsync(2500);

        await localPay.ApproveAsync(payment.Id);

        var deliveries = await localPay.Shop.WaitForAsync(3);
        deliveries.Should().HaveCount(3);
        deliveries.Select(d => d.Id).Distinct().Should().ContainSingle("a retry is the same event");
    }

    [Fact]
    public async Task Scenario_01_declines_an_approval()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2501);
        payment.Scenario.Should().Be("decline");

        await localPay.ApproveAsync(payment.Id);

        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("failed");
        (await localPay.Shop.WaitForAsync(1)).Should().ContainSingle().Which.Type.Should().Be("payment.failed");
    }

    [Fact]
    public async Task Scenario_02_sends_the_webhook_late()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2502);

        await localPay.ApproveAsync(payment.Id);

        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("succeeded", "the payment itself is done at once");
        localPay.Shop.Received.Should().BeEmpty("the webhook waits 1 s in these tests");
        (await localPay.Shop.WaitForAsync(1)).Should().ContainSingle().Which.Type.Should().Be("payment.succeeded");
    }

    [Fact]
    public async Task Scenario_03_sends_the_webhook_twice_with_the_same_event_id()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2503);

        await localPay.ApproveAsync(payment.Id);

        var webhooks = await localPay.Shop.WaitForAsync(2);
        webhooks.Should().HaveCount(2);
        webhooks.Select(w => w.Id).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Scenario_04_sends_no_webhook()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2504);

        await localPay.ApproveAsync(payment.Id);

        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("succeeded", "only the status API tells");
        (await localPay.Shop.WaitForAsync(1, TimeSpan.FromMilliseconds(500))).Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario_05_completes_the_payment_when_the_shop_expires_it()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2505);

        var expire = await localPay.ExpireAsync(payment.Id);

        expire.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await expire.Content.ReadFromJsonAsync<PaymentResponse>(Ct))!.Status.Should().Be("succeeded");
        (await localPay.Shop.WaitForAsync(1)).Should().ContainSingle().Which.Type.Should().Be("payment.succeeded");
    }

    [Fact]
    public async Task Scenario_06_fails_the_first_create_and_a_retry_with_the_same_key_creates_one_payment()
    {
        await using var localPay = new LocalPayUnderTest();

        (await localPay.CreateRawAsync(2506, "order-6", Ct)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var retried = await localPay.CreateAsync(2506, idempotencyKey: "order-6");
        var again = await localPay.CreateAsync(2506, idempotencyKey: "order-6");
        again.Id.Should().Be(retried.Id);
        retried.Status.Should().Be("requires_payment");
    }

    [Fact]
    public async Task Scenario_07_creates_the_payment_but_answers_too_late_and_a_retry_gets_it_at_once()
    {
        await using var localPay = new LocalPayUnderTest();

        // The shop gives up waiting (the answer takes 2 s in these tests)...
        using (var impatient = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            impatient.CancelAfter(TimeSpan.FromMilliseconds(300));
            await FluentActions.Awaiting(() => localPay.CreateRawAsync(2507, "order-7", impatient.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        // ...but the payment exists: the retry with the same key gets it without waiting.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var retried = await localPay.CreateAsync(2507, idempotencyKey: "order-7");
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        retried.Status.Should().Be("requires_payment");
    }

    [Fact]
    public async Task Scenario_08_follows_the_success_with_a_stale_failure_dated_before_it()
    {
        await using var localPay = new LocalPayUnderTest();
        var payment = await localPay.CreateAsync(2508);

        await localPay.ApproveAsync(payment.Id);

        var webhooks = await localPay.Shop.WaitForAsync(2);
        webhooks.Select(webhook => webhook.Type).Should().Equal("payment.succeeded", "payment.failed");
        webhooks[1].Id.Should().NotBe(webhooks[0].Id, "a different event, not a duplicate");
        webhooks[1].Created.Should().BeBefore(webhooks[0].Created);
        webhooks.Should().OnlyContain(webhook => IsSignedCorrectly(webhook));
        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("succeeded", "the stale event changes nothing at LocalPay");
    }

    [Fact]
    public async Task Headless_mode_pays_without_a_browser()
    {
        await using var localPay = new LocalPayUnderTest(headless: true);
        var payment = await localPay.CreateAsync(2500);

        (await localPay.Shop.WaitForAsync(1)).Should().ContainSingle().Which.Type.Should().Be("payment.succeeded");
        (await localPay.StatusAsync(payment.Id)).Status.Should().Be("succeeded");
    }

    [Fact]
    public async Task The_api_is_described_by_an_OpenAPI_document()
    {
        await using var localPay = new LocalPayUnderTest();

        var document = await localPay.Api.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        document.Should().Contain("\"CreatePayment\"").And.Contain("\"ExpirePayment\"").And.Contain("\"CreateRefund\"").And.Contain("LocalPay-Signature");
    }

    private static async Task<HttpResponseMessage> RefundAsync(LocalPayUnderTest localPay, string paymentId, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/refunds") { Content = JsonContent.Create(new CreateRefund(paymentId)) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await localPay.Api.SendAsync(request, Ct);
    }

    /// <summary>Checks the signature the way a shop must: HMAC-SHA256 over "t.body" with the shared secret.</summary>
    private static bool IsSignedCorrectly(ReceivedWebhook webhook)
    {
        var parts = webhook.Signature.Split(',').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(LocalPayUnderTest.Secret),
            Encoding.UTF8.GetBytes($"{parts["t"]}.{webhook.Body}")));

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts["t"], CultureInfo.InvariantCulture));
        return parts["v1"] == expected && age < TimeSpan.FromMinutes(1);
    }
}
