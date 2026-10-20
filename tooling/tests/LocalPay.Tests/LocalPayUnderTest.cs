using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Tooling.LocalPay;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketing.Tooling.LocalPay.Tests;

/// <summary>LocalPay in memory. Its webhooks go to <see cref="Shop"/> instead of a real endpoint.</summary>
internal sealed class LocalPayUnderTest : IAsyncDisposable
{
    public const string Secret = "test-secret";

    private readonly WebApplicationFactory<LocalPayEntryPoint> _factory;

    public LocalPayUnderTest(bool headless = false)
    {
        _factory = new WebApplicationFactory<LocalPayEntryPoint>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Webhooks:Url", "http://shop.test/webhooks/localpay");
            host.UseSetting("Webhooks:Secret", Secret);
            host.UseSetting("Webhooks:RetryDelay", "00:00:00.050");
            host.UseSetting("Scenarios:WebhookDelay", "00:00:01");
            host.UseSetting("Scenarios:CreateDelay", "00:00:02");
            host.UseSetting("Headless:Enabled", headless ? "true" : "false");
            host.UseSetting("Headless:MinDelay", "00:00:00.050");
            host.UseSetting("Headless:MaxDelay", "00:00:00.100");
            host.UseSetting("Checkout:ShopName", "Test Shop");
            host.ConfigureTestServices(services =>
                services.AddHttpClient(WebhookDispatcher.ClientName).ConfigurePrimaryHttpMessageHandler(() => Shop));
        });

        Api = _factory.CreateClient();
        Browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public StubShop Shop { get; } = new();

    public HttpClient Api { get; }

    /// <summary>The fan's browser: sees redirects instead of following them.</summary>
    public HttpClient Browser { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<PaymentResponse> CreateAsync(long amountMinorUnits, DateTimeOffset? expiresAt = null, string? idempotencyKey = null, string returnUrl = "http://shop.test/orders/1")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePayment(
                "order-" + Guid.NewGuid().ToString("N")[..8],
                amountMinorUnits,
                "EUR",
                returnUrl,
                expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30))),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        var response = await Api.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>(Ct))!;
    }

    /// <summary>POST /v1/payments without judging the answer, for the scenarios that misbehave on create.</summary>
    public async Task<HttpResponseMessage> CreateRawAsync(long amountMinorUnits, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new CreatePayment("order-raw", amountMinorUnits, "EUR", "http://shop.test/orders/1", DateTimeOffset.UtcNow.AddMinutes(30))),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await Api.SendAsync(request, cancellationToken);
    }

    public async Task<PaymentResponse> StatusAsync(string paymentId) =>
        (await Api.GetFromJsonAsync<PaymentResponse>(new Uri($"/v1/payments/{paymentId}", UriKind.Relative), Ct))!;

    public Task<HttpResponseMessage> ApproveAsync(string paymentId) =>
        Browser.PostAsync(new Uri($"/checkout/{paymentId}/approve", UriKind.Relative), null, Ct);

    public Task<HttpResponseMessage> DeclineAsync(string paymentId) =>
        Browser.PostAsync(new Uri($"/checkout/{paymentId}/decline", UriKind.Relative), null, Ct);

    public Task<HttpResponseMessage> ExpireAsync(string paymentId) =>
        Api.PostAsync(new Uri($"/v1/payments/{paymentId}/expire", UriKind.Relative), null, Ct);

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        Browser.Dispose();
        await _factory.DisposeAsync();
    }
}

/// <summary>The shop's webhook endpoint: records what arrives and answers with the queued status codes (200 when none is queued).</summary>
internal sealed class StubShop : HttpMessageHandler
{
    private readonly ConcurrentQueue<HttpStatusCode> _answers = new();

    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

    public void AnswerNext(params HttpStatusCode[] statusCodes)
    {
        foreach (var statusCode in statusCodes)
        {
            _answers.Enqueue(statusCode);
        }
    }

    /// <summary>Waits until <paramref name="count"/> webhooks have arrived, then a moment longer to catch any extra one.</summary>
    public async Task<IReadOnlyList<ReceivedWebhook>> WaitForAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Received.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Task.Delay(200, TestContext.Current.CancellationToken);
        return [.. Received];
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Received.Enqueue(new ReceivedWebhook(body, request.Headers.GetValues(WebhookSignature.Header).Single()));
        return new HttpResponseMessage(_answers.TryDequeue(out var statusCode) ? statusCode : HttpStatusCode.OK);
    }
}

internal sealed record ReceivedWebhook(string Body, string Signature)
{
    private JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string Id => Json.GetProperty("id").GetString()!;

    public string Type => Json.GetProperty("type").GetString()!;

    public JsonElement Data => Json.GetProperty("data");

    public DateTimeOffset Created => Json.GetProperty("created").GetDateTimeOffset();
}
