using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Catalog.Events;

public sealed record PriceCategoryInput(string Name, long PriceMinorUnits);

/// <summary>Puts a section's places, or only the listed rows of it, in a price category.</summary>
public sealed record PriceAssignmentInput(string Section, string PriceCategory, IReadOnlyList<string>? Rows = null);

/// <summary>An organizer creates an event in Draft at one of its venues. Also the request body of POST /events.</summary>
public sealed record CreateEvent(
    Guid OrganizerId,
    Guid VenueId,
    string Name,
    DateTimeOffset StartsAt,
    string Currency,
    IReadOnlyList<PriceCategoryInput> PriceCategories,
    IReadOnlyList<PriceAssignmentInput> Seating) : ICommand<Guid>;

internal sealed class CreateEventValidator : AbstractValidator<CreateEvent>
{
    public CreateEventValidator()
    {
        RuleFor(command => command.OrganizerId).NotEmpty();
        RuleFor(command => command.VenueId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Currency).Must(code => CurrencyCode.Create(code).IsSuccess)
            .WithMessage("Use a three-letter ISO 4217 currency code, e.g. EUR.");
        RuleFor(command => command.PriceCategories).NotEmpty();
        RuleFor(command => command.PriceCategories.Count).LessThanOrEqualTo(50).WithName(nameof(CreateEvent.PriceCategories));
        RuleForEach(command => command.PriceCategories).ChildRules(category =>
        {
            category.RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
            category.RuleFor(c => c.PriceMinorUnits).GreaterThanOrEqualTo(0);
        });
        RuleFor(command => command.PriceCategories)
            .Must(categories => categories.Select(c => c.Name).Distinct().Count() == categories.Count)
            .WithMessage("Price category names must be unique.");
        RuleFor(command => command.Seating).NotEmpty();
        RuleFor(command => command)
            .Must(command => command.Seating.All(rule => command.PriceCategories.Any(c => c.Name == rule.PriceCategory)))
            .WithName(nameof(CreateEvent.Seating))
            .WithMessage("Every seating rule must use one of the event's price categories.");
        RuleFor(command => command.Seating)
            .Must(seating => seating.Where(rule => rule.Rows is not { Count: > 0 }).GroupBy(rule => rule.Section).All(group => group.Count() == 1))
            .WithMessage("A section can have only one rule without rows.");
        RuleFor(command => command.Seating)
            .Must(seating => seating
                .SelectMany(rule => (rule.Rows ?? []).Select(row => (rule.Section, row)))
                .GroupBy(place => place)
                .All(group => group.Count() == 1))
            .WithMessage("A row can be in only one seating rule.");
    }
}

/// <summary>Only members of the organizer may create its events (ADR-0023).</summary>
internal sealed class CreateEventAuthorizer : ICommandAuthorizer<CreateEvent>
{
    public ValueTask<bool> IsAllowedAsync(CreateEvent command, IExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(context.IsAuthenticated && context.OrganizerIds.Contains(command.OrganizerId));
}

internal sealed class CreateEventHandler(CatalogDbContext db) : ICommandHandler<CreateEvent, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateEvent command, CancellationToken cancellationToken)
    {
        // Another organizer's venue is reported as missing, not as forbidden: its existence isn't ours to know.
        var venue = await db.Venues
            .Include(v => v.Sections).ThenInclude(s => s.Seats)
            .AsSplitQuery()
            .SingleOrDefaultAsync(v => v.Id == command.VenueId && v.OrganizerId == command.OrganizerId, cancellationToken);

        if (venue is null)
        {
            return Error.NotFound("venue.not_found", "The venue does not exist.");
        }

        var created = Event.Create(
            venue,
            command.Name,
            command.StartsAt,
            CurrencyCode.Create(command.Currency).Value,
            command.PriceCategories,
            command.Seating);

        if (created.IsFailure)
        {
            return created.Error;
        }

        db.Events.Add(created.Value);
        return created.Value.Id;
    }
}
