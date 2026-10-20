using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using EventTicketing.Payments.Application;
using EventTicketing.Payments.Infrastructure.Providers;
using Microsoft.Extensions.Options;
using Stripe;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1b P3 (ADR-0050): the Stripe adapter without a Stripe account. Its calls go to <c>stripe-mock</c>,
/// which checks every request against Stripe's API description: a wrong parameter fails here, not
/// in production. Webhooks are signed by the test the way Stripe documents it.
/// </summary>
public sealed class StripeAdapterTests(StripeMock stripeMock) : IClassFixture<StripeMock>
{
    private const string WebhookSecret = "whsec_test_secret";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_a_payment_makes_a_checkout_session_with_its_payment_page()
    {
        var created = await Provider().CreatePaymentAsync(
            new CreatePaymentRequest(Guid.NewGuid(), 2500, "EUR", "http://fansite.test/orders/1", DateTimeOffset.UtcNow.AddMinutes(30)),
            Ct);

        created.ProviderPaymentId.Should().StartWith("cs_");
        created.CheckoutUrl.Should().StartWith("https://");
    }

    [Fact]
    public async Task Status_cancel_list_and_refund_are_requests_Stripe_accepts()
    {
        var provider = Provider();
        var sessionId = (await provider.CreatePaymentAsync(
            new CreatePaymentRequest(Guid.NewGuid(), 2500, "EUR", "http://fansite.test/orders/1", DateTimeOffset.UtcNow.AddMinutes(30)), Ct)).ProviderPaymentId;

        // stripe-mock answers from fixtures, so only the requests are checked, not the session's story.
        Enum.IsDefined(await provider.GetStatusAsync(sessionId, Ct)).Should().BeTrue();
        Enum.IsDefined(await provider.CancelPaymentAsync(sessionId, Ct)).Should().BeTrue();
        (await provider.ListPaymentsAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, Ct).Take(5).ToListAsync(Ct)).Should().NotBeEmpty();
        var refund = await provider.RefundAsync(new RefundRequest(Guid.NewGuid(), sessionId, 2500, "EUR", "SeatsLost"), Ct);
        refund.Should().Match<RefundResult>(r => r.ProviderRefundId != null || r.Status == ProviderRefundStatus.Failed);
    }

    [Fact]
    public void A_signed_completed_checkout_is_a_successful_payment_for_our_order()
    {
        var orderId = Guid.NewGuid();
        var payload = Event("checkout.session.completed", $$"""
            { "id": "cs_test_1", "object": "checkout.session", "client_reference_id": "{{orderId}}",
              "amount_total": 2500, "currency": "eur", "status": "complete", "payment_status": "paid" }
            """);

        var webhook = Provider().VerifyWebhook(payload, Signed(payload, WebhookSecret, DateTimeOffset.UtcNow));

        webhook.Should().BeEquivalentTo(new
        {
            Type = ProviderWebhookTypes.PaymentSucceeded,
            ProviderPaymentId = "cs_test_1",
            Reference = orderId.ToString(),
            AmountMinorUnits = 2500L,
            Currency = "EUR",
        });
    }

    [Fact]
    public void A_refund_webhook_names_the_session_and_order_from_the_refunds_metadata()
    {
        var payload = Event("refund.updated", """
            { "id": "re_1", "object": "refund", "amount": 2500, "currency": "eur", "status": "succeeded",
              "metadata": { "checkoutSession": "cs_test_1", "orderId": "order-1" } }
            """);

        var webhook = Provider().VerifyWebhook(payload, Signed(payload, WebhookSecret, DateTimeOffset.UtcNow));

        webhook.Should().BeEquivalentTo(new
        {
            Type = ProviderWebhookTypes.RefundSucceeded,
            ProviderPaymentId = "cs_test_1",
            Reference = "order-1",
            AmountMinorUnits = 2500L,
            ProviderRefundId = "re_1",
        });
    }

    [Fact]
    public void Events_Payments_doesnt_act_on_are_acknowledged_and_ignored()
    {
        var payload = Event("checkout.session.expired", """{ "id": "cs_test_2", "object": "checkout.session", "status": "expired" }""");

        Provider().VerifyWebhook(payload, Signed(payload, WebhookSecret, DateTimeOffset.UtcNow))!.Type.Should().Be(ProviderWebhookTypes.Ignored);
    }

    [Fact]
    public void A_webhook_with_a_wrong_or_old_signature_is_refused()
    {
        var payload = Event("checkout.session.completed", """{ "id": "cs_test_3", "object": "checkout.session", "payment_status": "paid" }""");

        Provider().VerifyWebhook(payload, Signed(payload, "whsec_someone_else", DateTimeOffset.UtcNow)).Should().BeNull();
        Provider().VerifyWebhook(payload, Signed(payload, WebhookSecret, DateTimeOffset.UtcNow.AddMinutes(-10))).Should().BeNull();
        Provider().VerifyWebhook(payload + " ", Signed(payload, WebhookSecret, DateTimeOffset.UtcNow)).Should().BeNull();
        Provider().VerifyWebhook(payload, new Dictionary<string, string>()).Should().BeNull();
    }

    private StripeProvider Provider() => new(
        new StripeClient("sk_test_123", apiBase: stripeMock.ApiBase),
        Options.Create(new StripeOptions { SecretKey = "sk_test_123", WebhookSecret = WebhookSecret, ApiBase = stripeMock.ApiBase }),
        TimeProvider.System);

    private static string Event(string type, string dataObject) => $$"""
        { "id": "evt_{{Guid.NewGuid():N}}", "object": "event", "api_version": "{{StripeConfiguration.ApiVersion}}",
          "created": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}, "livemode": false, "type": "{{type}}",
          "data": { "object": {{dataObject}} } }
        """;

    /// <summary>Stripe's scheme: <c>Stripe-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "t.payload"}</c>.</summary>
    private static Dictionary<string, string> Signed(string payload, string secret, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));
        return new Dictionary<string, string> { [StripeProvider.SignatureHeader] = $"t={timestamp},v1={signature}" };
    }
}

/// <summary>Stripe's open-source mock server (ADR-0050), one container for the test class.</summary>
public sealed class StripeMock : IAsyncLifetime
{
    private const ushort Port = 12111;

    private readonly IContainer _container = new ContainerBuilder("stripe/stripe-mock:v0.206.0")
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(Port))
        .Build();

    public string ApiBase => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
