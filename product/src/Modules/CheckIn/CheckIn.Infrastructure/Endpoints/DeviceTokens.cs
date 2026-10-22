using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventTicketing.CheckIn.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace EventTicketing.CheckIn.Infrastructure.Endpoints;

/// <summary>A device's client credentials, as the back office showed them (D61).</summary>
public sealed record DeviceTokenRequest(string ClientId, string ClientSecret);

/// <summary>The device's access token, and for how many seconds it is good.</summary>
public sealed record DeviceTokenResponse(string AccessToken, int ExpiresIn);

/// <summary>
/// The scanner signs in here, and CheckInApi asks Keycloak (D62): a page can't call Keycloak's token
/// endpoint from another origin unless every device client allowed it (CORS), and the device needn't
/// know where Keycloak is. Only device clients: this is no general token proxy.
/// </summary>
internal static partial class DeviceTokens
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/device-token", async (DeviceTokenRequest request, IHttpClientFactory clients, IOptions<CheckInKeycloakOptions> options, CancellationToken cancellationToken) =>
            {
                if (!DeviceClient().IsMatch(request.ClientId ?? "") || string.IsNullOrEmpty(request.ClientSecret))
                {
                    return Results.Problem("Only a door device's client signs in here.", statusCode: StatusCodes.Status400BadRequest);
                }

                if (options.Value.BaseUrl is not { } keycloak)
                {
                    return Results.Problem("No identity provider is configured (CheckIn:Keycloak:BaseUrl).", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = request.ClientId!,
                    ["client_secret"] = request.ClientSecret,
                });
                var http = clients.CreateClient(nameof(DeviceTokens));
                using var answer = await http.PostAsync(new Uri(keycloak, $"realms/{options.Value.Realm}/protocol/openid-connect/token"), form, cancellationToken);
                if (answer.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                {
                    return Results.Problem("Keycloak refused the device's credentials: revoked, or mistyped.", statusCode: StatusCodes.Status401Unauthorized);
                }

                answer.EnsureSuccessStatusCode();
                var token = await answer.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                return Results.Ok(new DeviceTokenResponse(token.GetProperty("access_token").GetString()!, token.GetProperty("expires_in").GetInt32()));
            })
            .AllowAnonymous()
            .WithTags("Door")
            .WithName("GetDeviceToken")
            .WithSummary("Signs a door device in with its client credentials: an access token from Keycloak")
            .Produces<DeviceTokenResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    [GeneratedRegex("^device-[0-9a-f]{32}$")]
    private static partial Regex DeviceClient();
}
