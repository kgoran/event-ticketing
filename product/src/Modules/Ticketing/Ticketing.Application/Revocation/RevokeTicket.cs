using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Domain;
using EventTicketing.Ticketing.Contracts;
using FluentValidation;

namespace EventTicketing.Ticketing.Application.Revocation;

/// <summary>
/// An admin revokes a ticket (D63), so the door refuses it: TicketsRevoked tells CheckIn, which
/// puts it on the door list's revocations. In M4 chargebacks and cancelled events revoke tickets
/// the same way.
/// </summary>
public sealed record RevokeTicket(Guid TicketId, string Reason) : ICommand<Unit>;

internal sealed class RevokeTicketValidator : AbstractValidator<RevokeTicket>
{
    public RevokeTicketValidator()
    {
        RuleFor(command => command.TicketId).NotEqual(Guid.Empty);
        RuleFor(command => command.Reason).NotEmpty().MaximumLength(200);
    }
}

internal sealed class RevokeTicketAuthorizer : RoleAuthorizer<RevokeTicket>
{
    protected override string Role => Roles.Admin;
}

internal sealed class RevokeTicketHandler(ITicketRepository tickets, IIntegrationEventPublisher publisher, TimeProvider time)
    : ICommandHandler<RevokeTicket, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RevokeTicket command, CancellationToken cancellationToken)
    {
        var ticket = await tickets.FindAsync(command.TicketId, cancellationToken);
        if (ticket is null)
        {
            return Error.NotFound("ticket.not_found", "The ticket does not exist.");
        }

        var now = time.GetUtcNow();
        var revoked = ticket.Revoke(command.Reason, now);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        publisher.Publish(new TicketsRevoked(Guid.CreateVersion7(), now, ticket.EventId, [ticket.Id], command.Reason));
        TicketingTelemetry.TicketsRevoked.Add(1);
        return Unit.Value;
    }
}
