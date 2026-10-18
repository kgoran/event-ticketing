using EventTicketing.Catalog.Persistence;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Domain;
using FluentValidation;

namespace EventTicketing.Catalog.Events;

public sealed record PriceCategoryInput(string Name, long PriceMinorUnits);

public sealed record SectionInput(string Name, int Rows, int SeatsPerRow, string PriceCategory);

/// <summary>An organizer creates an event in Draft. Also the request body of POST /events.</summary>
public sealed record CreateEvent(
    Guid OrganizerId,
    string Name,
    DateTimeOffset StartsAt,
    string Currency,
    IReadOnlyList<PriceCategoryInput> PriceCategories,
    IReadOnlyList<SectionInput> Sections) : ICommand<Guid>;

internal sealed class CreateEventValidator : AbstractValidator<CreateEvent>
{
    public const int MaxSeats = 100_000;

    public CreateEventValidator()
    {
        RuleFor(command => command.OrganizerId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Currency).Must(code => CurrencyCode.Create(code).IsSuccess)
            .WithMessage("Use a three-letter ISO 4217 currency code, e.g. EUR.");
        RuleFor(command => command.PriceCategories).NotEmpty();
        RuleForEach(command => command.PriceCategories).ChildRules(category =>
        {
            category.RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
            category.RuleFor(c => c.PriceMinorUnits).GreaterThanOrEqualTo(0);
        });
        RuleFor(command => command.PriceCategories)
            .Must(categories => categories.Select(c => c.Name).Distinct().Count() == categories.Count)
            .WithMessage("Price category names must be unique.");
        RuleFor(command => command.Sections).NotEmpty();
        RuleForEach(command => command.Sections).ChildRules(section =>
        {
            section.RuleFor(s => s.Name).NotEmpty().MaximumLength(50);
            section.RuleFor(s => s.Rows).InclusiveBetween(1, 200);
            section.RuleFor(s => s.SeatsPerRow).InclusiveBetween(1, 200);
        });
        RuleFor(command => command)
            .Must(command => command.Sections.All(s => command.PriceCategories.Any(c => c.Name == s.PriceCategory)))
            .WithName(nameof(CreateEvent.Sections))
            .WithMessage("Every section must use one of the event's price categories.");
        RuleFor(command => command.Sections)
            .Must(sections => sections.Sum(s => (long)s.Rows * s.SeatsPerRow) <= MaxSeats)
            .WithMessage($"An event can have at most {MaxSeats} seats.");
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
    public Task<Result<Guid>> HandleAsync(CreateEvent command, CancellationToken cancellationToken)
    {
        var @event = Event.Create(
            command.OrganizerId,
            command.Name,
            command.StartsAt,
            CurrencyCode.Create(command.Currency).Value,
            command.PriceCategories,
            command.Sections);

        db.Events.Add(@event);
        return Task.FromResult<Result<Guid>>(@event.Id);
    }
}
