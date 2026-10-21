using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Tooling.Clients.Public;
using EventTicketing.Tooling.Clients.Public.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Bundle;

namespace EventTicketing.Tooling.LoadGenerator;

/// <summary>Where the AppHost's services are: from the environment (the Aspire resource sets it), or the launch-profile ports.</summary>
public sealed record LabUrls(Uri PublicApi, Uri Keycloak)
{
    public static LabUrls FromEnvironment() => new(
        Url("PUBLICAPI_URL", "http://localhost:5101"),
        Url("KEYCLOAK_URL", "http://localhost:8180"));

    private static Uri Url(string variable, string fallback) =>
        new((Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : fallback).TrimEnd('/') + "/");
}

/// <summary>
/// One request of the mix, through PublicApi's Kiota client (ADR-0037), measured. Browsing is
/// anonymous: the event list, the event page or availability. A hold is a lab fan holding the best
/// free place of a section and letting go of it at once, so places keep cycling and one fan never
/// holds two at a time (one hold per fan per event, ADR-0024). The clients share one plain
/// HttpClient, without Kiota's retry handler: a load generator that retried would hide failures.
/// </summary>
internal sealed class Traffic(LabUrls urls, LabFans fans, TargetEvents events, LoadStats stats, HttpClient http)
{
    private readonly PublicApiClient _anonymous = Client(http, urls, new AnonymousAuthenticationProvider());
    private readonly ConcurrentDictionary<string, PublicApiClient> _fanClients = new();

    public async Task SendOneAsync(LoadSettings settings, CancellationToken cancellationToken)
    {
        if (await events.ResolveAsync(_anonymous, settings.EventId, cancellationToken) is not { } target)
        {
            stats.Skipped(); // nothing on sale yet: run the seeder
            return;
        }

        if (Random.Shared.NextDouble() < settings.HoldShare)
        {
            await HoldAndReleaseAsync(target, settings.Section, cancellationToken);
        }
        else
        {
            await MeasureAsync(RequestKind.Browse, () => BrowseAsync(target.EventId, cancellationToken));
        }
    }

    private Task BrowseAsync(Guid eventId, CancellationToken cancellationToken) => Random.Shared.Next(3) switch
    {
        0 => _anonymous.Events.GetAsync(request => request.QueryParameters.PageSize = 20, cancellationToken),
        1 => _anonymous.Events[eventId].GetAsync(cancellationToken: cancellationToken),
        _ => _anonymous.Events[eventId].Availability.GetAsync(cancellationToken: cancellationToken),
    };

    private async Task HoldAndReleaseAsync(TargetEvent target, string? hotSection, CancellationToken cancellationToken)
    {
        if (!fans.TryTake(out var fan))
        {
            stats.Skipped(); // every lab fan is busy with a hold: the generator's limit, not the product's
            return;
        }

        try
        {
            // Signed in first, outside the measurement: a hold's latency is PublicApi's, not Keycloak's.
            try
            {
                await fans.TokenAsync(fan, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                stats.Started();
                stats.Completed(RequestKind.Hold, Outcome.Failed, TimeSpan.Zero);
                return;
            }

            var client = _fanClients.GetOrAdd(fan, name => Client(http, urls, new BaseBearerTokenAuthenticationProvider(new FanToken(fans, name))));
            var section = hotSection ?? target.Sections[Random.Shared.Next(target.Sections.Count)];
            HoldPlaced? placed = null;
            await MeasureAsync(RequestKind.Hold, async () => placed = await client.Events[target.EventId].Holds.PostAsync(
                new PlaceHoldRequest { Section = section, Quantity = 1 },
                cancellationToken: cancellationToken));

            if (placed?.HoldId is { } holdId)
            {
                await MeasureAsync(RequestKind.Release, () => client.Holds[holdId].DeleteAsync(cancellationToken: cancellationToken));
            }
        }
        finally
        {
            fans.Return(fan);
        }
    }

    private async Task MeasureAsync(RequestKind kind, Func<Task> request)
    {
        stats.Started();
        var started = Stopwatch.GetTimestamp();
        Outcome outcome;
        try
        {
            await request();
            outcome = Outcome.Ok;
        }
        catch (ApiException exception)
        {
            outcome = Classify(exception.ResponseStatusCode, exception.ResponseHeaders.ContainsKey("Retry-After"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            outcome = Outcome.Failed; // no connection, or no answer within the timeout
        }

        stats.Completed(kind, outcome, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// 409 and 429 are refusals. So is a 503 that says when to come back (Retry-After): the system
    /// turning work away on purpose, as a paused feature does (FF1). A 503 without it is a failure,
    /// such as the gateway with no instance to send the request to.
    /// </summary>
    internal static Outcome Classify(int statusCode, bool retryAfter = false) => statusCode switch
    {
        (int)HttpStatusCode.Conflict or (int)HttpStatusCode.TooManyRequests => Outcome.Refused,
        (int)HttpStatusCode.ServiceUnavailable when retryAfter => Outcome.Refused,
        >= 400 and < 500 => Outcome.ClientError,
        _ => Outcome.Failed,
    };

    private static PublicApiClient Client(HttpClient http, LabUrls urls, IAuthenticationProvider authentication) =>
        new(new DefaultRequestAdapter(authentication, httpClient: http) { BaseUrl = urls.PublicApi.ToString().TrimEnd('/') });

    private sealed class FanToken(LabFans fans, string fan) : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new();

        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
            fans.TokenAsync(fan, cancellationToken);
    }
}

/// <summary>The event under load and its sections (tribunes and the standing floor alike).</summary>
internal sealed record TargetEvent(Guid EventId, IReadOnlyList<string> Sections);

/// <summary>
/// Finds the event to load: the one the lab chose, or the first on-sale event PublicApi lists. Looked
/// up once per event; while there is none, asked again at most every few seconds, so an empty system
/// doesn't get a flood of lookups instead of the load.
/// </summary>
internal sealed class TargetEvents(TimeProvider time)
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _lookup = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TargetEvent> _known = new();
    private Guid? _default;
    private DateTimeOffset _nothingFoundAt = DateTimeOffset.MinValue;

    /// <summary>The event being loaded, if known yet.</summary>
    public Guid? Current(Guid? chosen) => chosen ?? _default;

    public async Task<TargetEvent?> ResolveAsync(PublicApiClient api, Guid? chosen, CancellationToken cancellationToken)
    {
        var eventId = chosen ?? _default;
        if (eventId is { } known && _known.TryGetValue(known, out var target))
        {
            return target;
        }

        if (time.GetUtcNow() - _nothingFoundAt < RetryAfter || !await _lookup.WaitAsync(0, cancellationToken))
        {
            return null; // looked recently, or another request is looking right now
        }

        try
        {
            eventId ??= _default = await FirstOnSaleAsync(api, cancellationToken);
            var sections = eventId is null
                ? null
                : (await api.Events[eventId.Value].Availability.GetAsync(cancellationToken: cancellationToken))?.Sections?
                    .Where(section => section.Total > 0).Select(section => section.Section!).ToList();

            if (sections is not { Count: > 0 })
            {
                _nothingFoundAt = time.GetUtcNow();
                return null;
            }

            return _known[eventId!.Value] = new TargetEvent(eventId.Value, sections);
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or TaskCanceledException)
        {
            _nothingFoundAt = time.GetUtcNow();
            return null;
        }
        finally
        {
            _lookup.Release();
        }
    }

    private static async Task<Guid?> FirstOnSaleAsync(PublicApiClient api, CancellationToken cancellationToken)
    {
        var events = await api.Events.GetAsync(request => request.QueryParameters.PageSize = 50, cancellationToken);
        return events?.Items?.FirstOrDefault(e => e.SalesPhase == "OnSale")?.EventId;
    }
}

/// <summary>
/// The realm's lab fans, lab01 to lab200 (their passwords equal their names), each busy with at most
/// one hold at a time. Tokens come from the development client's password grant, as for the seeder,
/// and are reused until shortly before they expire.
/// </summary>
internal sealed class LabFans(LabUrls urls, HttpClient http, TimeProvider time)
{
    public const int Count = 200;

    private readonly ConcurrentQueue<string> _free = new(Enumerable.Range(1, Count).Select(i => $"lab{i:00}"));
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    public bool TryTake(out string fan) => _free.TryDequeue(out fan!);

    public void Return(string fan) => _free.Enqueue(fan);

    public async Task<string> TokenAsync(string fan, CancellationToken cancellationToken)
    {
        if (_tokens.TryGetValue(fan, out var cached) && cached.ExpiresAt - time.GetUtcNow() > TimeSpan.FromSeconds(30))
        {
            return cached.Token;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "eventticketing-dev",
            ["username"] = fan,
            ["password"] = fan,
        });
        using var response = await http.PostAsync(new Uri(urls.Keycloak, "realms/eventticketing/protocol/openid-connect/token"), form, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var token = body.GetProperty("access_token").GetString()!;
        _tokens[fan] = (token, time.GetUtcNow().AddSeconds(body.GetProperty("expires_in").GetInt32()));
        return token;
    }
}
