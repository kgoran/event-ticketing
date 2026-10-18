using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using FluentValidation;
using NodaTime;

namespace EventTicketing.Catalog.Venues;

/// <param name="Rows">Seated sections only: rows from the front, each with its number of seats.</param>
/// <param name="Capacity">Standing sections only: how many places the area sells.</param>
/// <param name="X">Where the section starts on the seat map, in seat-sized grid units.</param>
public sealed record VenueSectionInput(
    string Code,
    string Name,
    SectionKind Kind,
    IReadOnlyList<VenueRowInput>? Rows = null,
    int? Capacity = null,
    int X = 0,
    int Y = 0);

/// <param name="FirstNumber">The number of the leftmost seat; the others count up from it.</param>
public sealed record VenueRowInput(string Label, int Seats, int FirstNumber = 1);

/// <summary>An organizer imports a venue's seat map. Also the request body of POST /venues.</summary>
public sealed record ImportVenue(
    Guid OrganizerId,
    string Name,
    string TimeZone,
    IReadOnlyList<VenueSectionInput> Sections) : ICommand<Guid>;

internal sealed class ImportVenueValidator : AbstractValidator<ImportVenue>
{
    /// <summary>Seats plus standing places. Inventory keeps one row per place (ADR-0011).</summary>
    public const int MaxPlaces = 100_000;

    public ImportVenueValidator()
    {
        RuleFor(command => command.OrganizerId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.TimeZone).Must(BeAnIanaTimeZone)
            .WithMessage("Use an IANA time zone, e.g. Europe/Zagreb.");
        RuleFor(command => command.Sections).NotEmpty();
        RuleFor(command => command.Sections.Count).LessThanOrEqualTo(500).WithName(nameof(ImportVenue.Sections));
        RuleForEach(command => command.Sections).SetValidator(new SectionValidator());
        RuleFor(command => command.Sections)
            .Must(sections => sections.Select(s => s.Code).Distinct(StringComparer.Ordinal).Count() == sections.Count)
            .WithMessage("Section codes must be unique.");
        RuleFor(command => command.Sections)
            .Must(sections => sections.Sum(PlaceCount) <= MaxPlaces)
            .WithMessage($"A venue can have at most {MaxPlaces} places (seats plus standing).");
    }

    private static long PlaceCount(VenueSectionInput section) =>
        section.Kind == SectionKind.Standing ? section.Capacity ?? 0 : (section.Rows ?? []).Sum(row => (long)row.Seats);

    // Not TimeZoneInfo: with InvariantGlobalization it can't resolve IANA IDs on Windows, and on
    // Linux it depends on the image shipping tzdata.
    private static bool BeAnIanaTimeZone(string timeZone) =>
        !string.IsNullOrWhiteSpace(timeZone) && DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) is not null;

    private sealed class SectionValidator : AbstractValidator<VenueSectionInput>
    {
        public SectionValidator()
        {
            RuleFor(s => s.Code).NotEmpty().MaximumLength(20).Matches("^[A-Za-z0-9-]+$")
                .WithMessage("Section codes use letters, digits and dashes.");
            RuleFor(s => s.Name).NotEmpty().MaximumLength(100);
            RuleFor(s => s.Kind).IsInEnum();
            RuleFor(s => s.X).InclusiveBetween(0, 10_000);
            RuleFor(s => s.Y).InclusiveBetween(0, 10_000);

            When(s => s.Kind == SectionKind.Seated, () =>
            {
                RuleFor(s => s.Rows).NotEmpty().WithMessage("A seated section needs rows.");
                RuleFor(s => s.Capacity).Null().WithMessage("Only standing sections have a capacity.");
                RuleFor(s => s.Rows!.Count).LessThanOrEqualTo(500).When(s => s.Rows is not null).WithName(nameof(VenueSectionInput.Rows));
                RuleForEach(s => s.Rows).ChildRules(row =>
                {
                    row.RuleFor(r => r.Label).NotEmpty().MaximumLength(20);
                    row.RuleFor(r => r.Seats).InclusiveBetween(1, 500);
                    row.RuleFor(r => r.FirstNumber).InclusiveBetween(0, 10_000);
                });
                RuleFor(s => s.Rows)
                    .Must(rows => rows!.Select(r => r.Label).Distinct(StringComparer.Ordinal).Count() == rows!.Count)
                    .When(s => s.Rows is not null)
                    .WithMessage("Row labels must be unique within a section.");
            });

            When(s => s.Kind == SectionKind.Standing, () =>
            {
                RuleFor(s => s.Capacity).NotNull().InclusiveBetween(1, MaxPlaces);
                RuleFor(s => s.Rows).Empty().WithMessage("A standing section has no rows.");
            });
        }
    }
}

/// <summary>Only members of the organizer may import venues for it (ADR-0023).</summary>
internal sealed class ImportVenueAuthorizer : ICommandAuthorizer<ImportVenue>
{
    public ValueTask<bool> IsAllowedAsync(ImportVenue command, IExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(context.IsAuthenticated && context.OrganizerIds.Contains(command.OrganizerId));
}

internal sealed class ImportVenueHandler(CatalogDbContext db, TimeProvider time) : ICommandHandler<ImportVenue, Guid>
{
    public Task<Result<Guid>> HandleAsync(ImportVenue command, CancellationToken cancellationToken)
    {
        var venue = Venue.Import(command.OrganizerId, command.Name, command.TimeZone, command.Sections, time.GetUtcNow());
        db.Venues.Add(venue);
        return Task.FromResult<Result<Guid>>(venue.Id);
    }
}
