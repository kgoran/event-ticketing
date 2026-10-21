using EventTicketing.Catalog.Events;
using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Infrastructure.Caching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace EventTicketing.Catalog.Browse;

public sealed record EventSummary(
    Guid EventId,
    string Name,
    DateTimeOffset StartsAt,
    string TimeZone,
    string VenueName,
    string Currency,
    long PriceFromMinorUnits,
    DateTimeOffset? OnSaleAt,
    string SalesPhase);

/// <param name="HasMore">Whether a next page exists. There is no total: counting every match on each request costs more than it's worth.</param>
public sealed record EventListPage(IReadOnlyList<EventSummary> Items, int Page, int PageSize, bool HasMore);

public sealed record PublicPriceCategory(Guid PriceCategoryId, string Name, long PriceMinorUnits);

/// <param name="SeatMapUrl">Where the seat-map geometry is; the URL changes whenever the map does, so it can be cached for good.</param>
public sealed record EventPage(
    Guid EventId,
    string Name,
    DateTimeOffset StartsAt,
    string TimeZone,
    Guid VenueId,
    string VenueName,
    string Currency,
    DateTimeOffset? OnSaleAt,
    string SalesPhase,
    IReadOnlyList<PublicPriceCategory> PriceCategories,
    string SeatMapUrl);

public sealed record SeatMapSeat(Guid SeatId, string Row, int Number, int X, int Y, Guid PriceCategoryId);

/// <param name="Capacity">Standing areas only.</param>
/// <param name="PriceCategoryId">Standing areas only; seats carry their own.</param>
public sealed record SeatMapSection(string Code, string Name, string Kind, int X, int Y, int? Capacity, Guid? PriceCategoryId, IReadOnlyList<SeatMapSeat> Seats);

public sealed record SeatMapCategory(Guid PriceCategoryId, string Name);

/// <summary>
/// The layout and each seat's price category, without prices: prices can change, and this
/// response is cached for good. The client combines it with Inventory's availability and the
/// event page's prices (ADR-0007 rule 4).
/// </summary>
public sealed record SeatMap(Guid EventId, string Version, IReadOnlyList<SeatMapCategory> PriceCategories, IReadOnlyList<SeatMapSection> Sections);

/// <summary>Fans browse published events in PublicApi, signed in or not.</summary>
internal static class BrowseEndpoints
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>The output cache's tags (D52): everything browsing, the list, and one event's answers.</summary>
    public const string BrowseTag = "catalog-browse";

    public const string ListTag = "catalog-list";

    public const string EventTag = "catalog-event:{eventId}";

    /// <summary>
    /// Short: on-sale times and prices change, and a CDN or the output cache absorbs the load
    /// (ADR-0028). Never past the moment an answer changes by itself, such as an on-sale opening.
    /// </summary>
    private static readonly TimeSpan BrowseLifetime = TimeSpan.FromSeconds(30);

    private const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapGroup("/events").WithTags("Browse").AllowAnonymous();

        // Every caller gets the same answers here, so the shared cache serves them (ADR-0028, D52), for
        // as long as Cache-Control lets a browser keep them.
        events.MapGet("/", ListAsync)
            .CachedForEveryone(BrowseLifetime, BrowseTag, ListTag)
            .WithName("ListEvents")
            .WithSummary("Published events that haven't started, soonest first; with q, the best matches first")
            .Produces<EventListPage>()
            .ProducesValidationProblem();

        events.MapGet("/{eventId:guid}", GetAsync)
            .CachedForEveryone(BrowseLifetime, BrowseTag, EventTag)
            .WithName("GetEvent")
            .WithSummary("A published event: venue, prices, sales phase and where its seat map is")
            .Produces<EventPage>()
            .Produces(StatusCodes.Status404NotFound);

        events.MapGet("/{eventId:guid}/seat-map/{version}", GetSeatMapAsync)
            .CachedForEveryone(TimeSpan.FromDays(1), BrowseTag, EventTag) // a version never changes; a day keeps Valkey small
            .WithName("GetSeatMap")
            .WithSummary("The seat-map geometry of a published event, at the version the event page links to")
            .Produces<SeatMap>()
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <param name="q">Words to search for in the event and venue names (web search syntax: "quoted phrases", -excluded).</param>
    private static async Task<IResult> ListAsync(
        CatalogDbContext db,
        TimeProvider time,
        HttpResponse response,
        string? q,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var pageNumber = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (pageNumber < 1 || size is < 1 or > MaxPageSize)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["page"] = pageNumber < 1 ? ["Pages start at 1."] : [],
                ["pageSize"] = size is < 1 or > MaxPageSize ? [$"Use 1 to {MaxPageSize}."] : [],
            }.Where(error => error.Value.Length > 0).ToDictionary());
        }

        var now = time.GetUtcNow();
        var published = db.Events.AsNoTracking().Where(e => e.Status == EventStatus.Published && e.StartsAt > now);

        IQueryable<Event> ordered;
        if (string.IsNullOrWhiteSpace(q))
        {
            ordered = published.OrderBy(e => e.StartsAt).ThenBy(e => e.Id);
        }
        else
        {
            // websearch_to_tsquery accepts whatever a fan types without syntax errors. It must stay
            // inside the lambdas: EF Core can only translate it as part of the query expression.
            ordered = published
                .Where(e => EF.Property<NpgsqlTsVector>(e, CatalogDbContext.SearchVectorProperty)
                    .Matches(EF.Functions.WebSearchToTsQuery(CatalogDbContext.SearchConfiguration, q)))
                .OrderByDescending(e => EF.Property<NpgsqlTsVector>(e, CatalogDbContext.SearchVectorProperty)
                    .Rank(EF.Functions.WebSearchToTsQuery(CatalogDbContext.SearchConfiguration, q)))
                .ThenBy(e => e.StartsAt)
                .ThenBy(e => e.Id);
        }

        // One row more than the page tells whether there is a next page.
        var rows = await ordered
            .Skip((pageNumber - 1) * size)
            .Take(size + 1)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.StartsAt,
                e.VenueName,
                e.Currency,
                e.OnSaleAt,
                e.Status,
                e.TimeZone,
                PriceFrom = e.PriceCategories.Min(c => c.PriceMinorUnits),
            })
            .ToListAsync(cancellationToken);

        var items = rows.Take(size)
            .Select(row => new EventSummary(
                row.Id,
                row.Name,
                row.StartsAt,
                row.TimeZone,
                row.VenueName,
                row.Currency,
                row.PriceFrom,
                row.OnSaleAt,
                Event.SalesPhaseOf(row.Status, row.OnSaleAt, row.StartsAt, now).ToString()))
            .ToList();

        Cacheable(response, now, rows.Take(size).Select(row => NextChange(row.Status, row.OnSaleAt, row.StartsAt, now)));
        return Results.Ok(new EventListPage(items, pageNumber, size, rows.Count > size));
    }

    private static async Task<IResult> GetAsync(Guid eventId, CatalogDbContext db, TimeProvider time, HttpResponse response, CancellationToken cancellationToken)
    {
        var @event = await db.Events
            .AsNoTracking()
            .Include(e => e.PriceCategories)
            .SingleOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Published, cancellationToken);

        if (@event is null)
        {
            return Results.NotFound();
        }

        var now = time.GetUtcNow();
        Cacheable(response, now, [NextChange(@event.Status, @event.OnSaleAt, @event.StartsAt, now)]);
        return Results.Ok(new EventPage(
            @event.Id,
            @event.Name,
            @event.StartsAt,
            @event.TimeZone,
            @event.VenueId,
            @event.VenueName,
            @event.Currency,
            @event.OnSaleAt,
            @event.SalesPhaseAt(now).ToString(),
            [.. @event.PriceCategories.OrderByDescending(c => c.PriceMinorUnits).ThenBy(c => c.Name).Select(c => new PublicPriceCategory(c.Id, c.Name, c.PriceMinorUnits))],
            $"/events/{@event.Id}/seat-map/{@event.SeatMapVersion}"));
    }

    /// <summary>When an event's sales phase changes by itself: its on-sale opening, or its start.</summary>
    private static DateTimeOffset? NextChange(EventStatus status, DateTimeOffset? onSaleAt, DateTimeOffset startsAt, DateTimeOffset now) =>
        status != EventStatus.Published ? null
        : onSaleAt is { } opens && now < opens ? opens
        : now < startsAt ? startsAt
        : null;

    /// <summary>Cached for the browse lifetime, or until the soonest of <paramref name="changes"/>, by browsers and the output cache alike.</summary>
    private static void Cacheable(HttpResponse response, DateTimeOffset now, IEnumerable<DateTimeOffset?> changes)
    {
        var soonest = changes.Where(change => change is not null).DefaultIfEmpty(null).Min();
        response.Headers.CacheControl = SharedOutputCache.CacheControl(BrowseLifetime, soonest, now);
        if (soonest is { } moment)
        {
            SharedOutputCache.ExpiresBy(response.HttpContext, moment);
        }
    }

    private static async Task<IResult> GetSeatMapAsync(Guid eventId, string version, CatalogDbContext db, HttpResponse response, CancellationToken cancellationToken)
    {
        var @event = await db.Events
            .AsNoTracking()
            .Include(e => e.PriceCategories)
            .Include(e => e.PriceAssignments)
            .AsSplitQuery()
            .SingleOrDefaultAsync(e => e.Id == eventId && e.Status == EventStatus.Published, cancellationToken);

        // An old version is gone for good, so a 404 may be cached too; the event page has the current URL.
        if (@event is null || @event.SeatMapVersion != version)
        {
            return Results.NotFound();
        }

        var venue = await db.Venues
            .AsNoTracking()
            .Include(v => v.Sections).ThenInclude(s => s.Seats)
            .AsSplitQuery()
            .SingleAsync(v => v.Id == @event.VenueId, cancellationToken);

        response.Headers.CacheControl = ImmutableCacheControl;
        return Results.Ok(SeatMaps.Of(@event, venue));
    }
}
