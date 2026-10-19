using EventTicketing.FanSite.Authentication;
using EventTicketing.FanSite.PublicApi;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace EventTicketing.FanSite.Api;

/// <summary>PublicApi's Kiota client (ADR-0037), sending the signed-in fan's access token.</summary>
internal static class PublicApiRegistration
{
    private const string HttpClientName = "publicapi";

    public static WebApplicationBuilder AddPublicApiClient(this WebApplicationBuilder builder)
    {
        // "http://publicapi" is resolved by service discovery: Aspire passes PublicApi's address.
        var baseUrl = builder.Configuration["PublicApi:BaseUrl"] ?? "http://publicapi";

        // Holds and checkouts are POSTs: retrying one after a timeout could act twice, so only
        // reads are retried. PublicApi answers a repeated checkout with the same order, but a
        // repeated hold with "you already hold seats". This applies to every client's standard
        // resilience handler (ServiceDefaults adds one to each); PublicApi's is the only one here.
        builder.Services.ConfigureAll<HttpStandardResilienceOptions>(options => options.Retry.DisableForUnsafeHttpMethods());
        builder.Services.AddHttpClient(HttpClientName);

        // Scoped to the circuit, so the token provider finds the fan of this circuit.
        builder.Services.AddScoped(services =>
        {
            var httpClient = services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var authentication = new FanAuthenticationProvider(services.GetRequiredService<FanAccessToken>());
            return new PublicApiClient(new HttpClientRequestAdapter(authentication, httpClient: httpClient) { BaseUrl = baseUrl });
        });

        return builder;
    }

    /// <summary>Adds the fan's bearer token when there is one; browsing works without.</summary>
    private sealed class FanAuthenticationProvider(FanAccessToken accessToken) : IAuthenticationProvider
    {
        public async Task AuthenticateRequestAsync(
            RequestInformation request,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            if (await accessToken.GetAsync(cancellationToken) is { } token)
            {
                request.Headers.Add("Authorization", "Bearer " + token);
            }
        }
    }
}
