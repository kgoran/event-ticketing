using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.FanSite.Authentication;

/// <summary>Sign in and sign out. Plain endpoints, because a Blazor circuit can't set cookies.</summary>
internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account");

        account.MapGet("/login", (string? returnUrl) =>
            TypedResults.Challenge(new AuthenticationProperties { RedirectUri = LocalUrl(returnUrl) }));

        // A form POST with an antiforgery token ([FromForm] makes the antiforgery middleware check it),
        // so another site can't sign a fan out.
        account.MapPost("/logout", async ([FromForm] string? returnUrl, HttpContext context, FanTokenStore store) =>
        {
            if (context.User.FindFirstValue(FanSessions.SessionClaim) is { } sessionId)
            {
                context.Items[FanSessions.IdTokenItem] = (await store.GetAsync(sessionId, context.RequestAborted))?.IdToken;
                await store.RemoveAsync(sessionId, context.RequestAborted);
            }

            return TypedResults.SignOut(
                new AuthenticationProperties { RedirectUri = LocalUrl(returnUrl) },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        });

        return app;
    }

    /// <summary>Only a path on this site, so the sign-in round trip can't be used to send fans elsewhere.</summary>
    internal static string LocalUrl(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
