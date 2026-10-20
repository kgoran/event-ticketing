using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketing.Organizers.Identity;

/// <summary>
/// The identity provider's side of organizer membership (ADR-0027): a group per organizer, whose
/// members get the organizer's ID in their tokens. Organizers is the source of truth and keeps the
/// provider in step through this port; an outbound integration behind an adapter, like a payment provider.
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>Creates the organizer's group unless it exists: safe to repeat.</summary>
    Task EnsureOrganizerGroupAsync(Guid organizerId, CancellationToken cancellationToken);

    /// <summary>Puts the user in the organizer's group, or takes them out: safe to repeat either way.</summary>
    Task SetMembershipAsync(Guid organizerId, string userId, bool isMember, CancellationToken cancellationToken);
}

/// <summary>Configuration section "Organizers:Keycloak". Without a client secret the directory isn't kept in step.</summary>
public sealed class KeycloakDirectoryOptions
{
    /// <summary>Keycloak's base address, e.g. http://localhost:8180.</summary>
    public Uri? BaseUrl { get; set; }

    public string Realm { get; set; } = "eventticketing";

    /// <summary>A service account allowed to manage users and groups, and nothing else.</summary>
    public string ClientId { get; set; } = "eventticketing-organizers";

    public string? ClientSecret { get; set; }

    /// <summary>The parent group whose children are the organizers (the token mapper uses their names).</summary>
    public string ParentGroup { get; set; } = "organizers";
}

/// <summary>
/// Keycloak's admin REST API with a client-credentials token. The only Keycloak-specific code in
/// the product (ADR-0027); another provider would be another implementation of the port.
/// </summary>
internal sealed partial class KeycloakDirectory(
    HttpClient http,
    KeycloakAdminToken token,
    IOptions<KeycloakDirectoryOptions> options,
    ILogger<KeycloakDirectory> logger)
    : IIdentityDirectory
{
    private KeycloakDirectoryOptions Settings => options.Value;

    public async Task EnsureOrganizerGroupAsync(Guid organizerId, CancellationToken cancellationToken) =>
        await GroupIdAsync(organizerId, create: true, cancellationToken);

    public async Task SetMembershipAsync(Guid organizerId, string userId, bool isMember, CancellationToken cancellationToken)
    {
        var groupId = await GroupIdAsync(organizerId, create: isMember, cancellationToken);
        if (groupId is null)
        {
            return; // no group, so nobody is in it
        }

        using var request = await AdminRequestAsync(
            isMember ? HttpMethod.Put : HttpMethod.Delete,
            $"users/{Uri.EscapeDataString(userId)}/groups/{groupId}",
            cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);

        // Removing someone who no longer exists in Keycloak leaves nothing to do.
        if (response.StatusCode == HttpStatusCode.NotFound && !isMember)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
        LogMembership(logger, userId, isMember ? "added to" : "removed from", organizerId);
    }

    /// <summary>The organizer's group ID; created first when <paramref name="create"/> and it doesn't exist.</summary>
    private async Task<string?> GroupIdAsync(Guid organizerId, bool create, CancellationToken cancellationToken)
    {
        var path = $"{Settings.ParentGroup}/{organizerId}";
        if (await FindGroupAsync(path, cancellationToken) is { } existing)
        {
            return existing;
        }

        if (!create)
        {
            return null;
        }

        var parentId = await FindGroupAsync(Settings.ParentGroup, cancellationToken)
            ?? throw new InvalidOperationException($"Keycloak has no '{Settings.ParentGroup}' group to put organizers under.");

        using var request = await AdminRequestAsync(HttpMethod.Post, $"groups/{parentId}/children", cancellationToken);
        request.Content = JsonContent.Create(new { name = organizerId.ToString() });
        using var response = await http.SendAsync(request, cancellationToken);

        // Created now, or by a concurrent sync a moment ago: either way, look it up.
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }

        return await FindGroupAsync(path, cancellationToken)
            ?? throw new InvalidOperationException($"Keycloak's group '{path}' is missing right after it was created.");
    }

    private async Task<string?> FindGroupAsync(string path, CancellationToken cancellationToken)
    {
        using var request = await AdminRequestAsync(HttpMethod.Get, $"group-by-path/{path}", cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var group = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return group.GetProperty("id").GetString();
    }

    private async Task<HttpRequestMessage> AdminRequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(Settings.BaseUrl!, $"admin/realms/{Settings.Realm}/{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAsync(http, cancellationToken));
        return request;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Keycloak: user {UserId} {Change} organizer {OrganizerId}")]
    private static partial void LogMembership(ILogger logger, string userId, string change, Guid organizerId);
}

/// <summary>The service account's client-credentials token, shared and reused until shortly before it expires.</summary>
internal sealed class KeycloakAdminToken(IOptions<KeycloakDirectoryOptions> options, TimeProvider time)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Value, DateTimeOffset ExpiresAt)? _token;

    private KeycloakDirectoryOptions Settings => options.Value;

    public async Task<string> GetAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } cached && cached.ExpiresAt - time.GetUtcNow() > TimeSpan.FromSeconds(30))
            {
                return cached.Value;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = Settings.ClientId,
                ["client_secret"] = Settings.ClientSecret!,
            });
            using var response = await http.PostAsync(new Uri(Settings.BaseUrl!, $"realms/{Settings.Realm}/protocol/openid-connect/token"), form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var value = body.GetProperty("access_token").GetString()!;
            _token = (value, time.GetUtcNow().AddSeconds(body.GetProperty("expires_in").GetInt32()));
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>When no identity provider is configured (e.g. module tests): membership stays a record only, as in M1a.</summary>
internal sealed partial class UnconfiguredDirectory(ILogger<UnconfiguredDirectory> logger) : IIdentityDirectory
{
    public Task EnsureOrganizerGroupAsync(Guid organizerId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetMembershipAsync(Guid organizerId, string userId, bool isMember, CancellationToken cancellationToken)
    {
        LogSkipped(logger, organizerId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No identity provider configured (Organizers:Keycloak): membership of organizer {OrganizerId} not synced")]
    private static partial void LogSkipped(ILogger logger, Guid organizerId);
}
