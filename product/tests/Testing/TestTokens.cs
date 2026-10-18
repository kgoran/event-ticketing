using System.Security.Cryptography;
using EventTicketing.Shared.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EventTicketing.Testing;

/// <summary>
/// Access tokens signed with a key that exists only for the test run, so host tests need no
/// running Keycloak (ADR-0027). They carry the same claims the realm issues: sub, roles, organizer_id.
/// </summary>
public static class TestTokens
{
    public const string Issuer = "https://tokens.test/realms/eventticketing";
    public const string Audience = "eventticketing-api";

    public static SymmetricSecurityKey SigningKey { get; } = new(RandomNumberGenerator.GetBytes(32)) { KeyId = "test-key" };

    public static string Create(
        string subject,
        IEnumerable<string>? roles = null,
        IEnumerable<Guid>? organizerIds = null,
        string audience = Audience,
        TimeSpan? lifetime = null,
        SecurityKey? signingKey = null)
    {
        var now = DateTime.UtcNow;
        var expires = now + (lifetime ?? TimeSpan.FromMinutes(5));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = expires < now ? expires.AddMinutes(-1) : now,
            NotBefore = expires < now ? expires.AddMinutes(-1) : now,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [TokenClaims.Subject] = subject,
                [TokenClaims.UserName] = subject,
                [TokenClaims.Roles] = (roles ?? []).ToArray(),
                [TokenClaims.OrganizerId] = (organizerIds ?? []).Select(id => id.ToString()).ToArray(),
            },
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Makes a host accept tokens from <see cref="Create"/> instead of discovering keys from Keycloak.</summary>
    public static IServiceCollection UseTestTokens(this IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null;
            options.ConfigurationManager = null;
            options.TokenValidationParameters.ValidIssuer = Issuer;
            options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
        });
}
