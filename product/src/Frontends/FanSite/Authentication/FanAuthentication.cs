using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using StackExchange.Redis;

namespace EventTicketing.FanSite.Authentication;

/// <summary>
/// Fans sign in at the identity provider with the authorization code flow and PKCE, using only
/// standard OpenID Connect (ADR-0027). The site is a confidential client, and its tokens never
/// reach the browser: the cookie carries a session key, and the tokens stay on the server in
/// <see cref="FanTokenStore"/>, where they are refreshed (ADR-0049).
/// </summary>
internal static class FanAuthentication
{
    /// <summary>Valkey's connection string's name, as the AppHost and the lab's chart give it.</summary>
    private const string Valkey = "valkey";

    public static WebApplicationBuilder AddFanAuthentication(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection("Authentication");

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "fansite";
                options.Cookie.SameSite = SameSiteMode.Lax;

                // The identity provider's own session lasts at most 10 hours (Keycloak's default).
                options.ExpireTimeSpan = TimeSpan.FromHours(10);
                options.SlidingExpiration = false;
                options.Events.OnValidatePrincipal = FanSessions.ValidateAsync;
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = settings["Authority"];
                options.RequireHttpsMetadata = settings.GetValue("RequireHttpsMetadata", true);
                options.ClientId = settings["ClientId"];
                options.ClientSecret = settings["ClientSecret"];
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = "roles";

                // The tokens go to FanTokenStore, not into the cookie.
                options.SaveTokens = false;

                // The callback comes back as a plain GET, so lax cookies work over plain HTTP in
                // development. The default (a form POST from the identity provider) needs
                // SameSite=None cookies, which browsers only accept over HTTPS.
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SameSite = SameSiteMode.Lax;

                options.Events.OnTokenValidated = FanSessions.StartAsync;
                options.Events.OnRedirectToIdentityProviderForSignOut = FanSessions.EndAtIdentityProviderAsync;
            });

        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();

        // Fans' tokens and the sign-in cookie's keys where every instance finds them (ADR-0049, D54):
        // in Valkey when the host has it, so a fan stays signed in on any instance and through a
        // deploy; in memory when the fan site runs alone. Not in readiness (D74): without Valkey the
        // fan site still shows events, and every instance would leave rotation together.
        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(Valkey)))
        {
            builder.AddRedisDistributedCache(Valkey, settings => settings.DisableHealthChecks = true);
            builder.Services.AddDataProtection().SetApplicationName("EventTicketing.FanSite");
            builder.Services.AddOptions<KeyManagementOptions>().Configure<IConnectionMultiplexer>((options, valkey) =>
                options.XmlRepository = new RedisXmlRepository(() => valkey.GetDatabase(), "fansite:data-protection-keys"));
        }
        else
        {
            builder.Services.AddDistributedMemoryCache();
        }

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<FanTokenStore>();
        builder.Services.AddSingleton<FanTokenRefresher>();
        builder.Services.AddScoped<FanAccessToken>();

        return builder;
    }
}
