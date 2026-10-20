using EventTicketing.Orders.Contracts;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Ticketing.Contracts;
using EventTicketing.Ticketing.Domain;

namespace EventTicketing.Ticketing.Application.Issuing;

/// <summary>
/// A confirmed order gets one ticket per place, each with a code signed by the event's key, and
/// TicketsIssued tells Notifications (and later CheckIn) in the same transaction (ADR-0051).
/// <list type="bullet">
/// <item>Safe to repeat: an order that already has tickets gets none again, and a unique index on
/// order and seat stops two deliveries racing.</item>
/// <item>Events can arrive out of order: an order for an event Ticketing hasn't seen yet fails,
/// and the outbox delivers it again later, by when EventPublished has been handled.</item>
/// </list>
/// </summary>
internal sealed class IssueTicketsOnOrderConfirmed(
    ITicketRepository tickets,
    ITicketedEvents events,
    IEventKeys keys,
    ITicketSigner signer,
    IIntegrationEventPublisher publisher,
    TimeProvider time)
    : IIntegrationEventHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken cancellationToken)
    {
        if (await tickets.AnyForOrderAsync(integrationEvent.OrderId, cancellationToken))
        {
            return;
        }

        var ticketedEvent = await events.FindAsync(integrationEvent.EventId, cancellationToken);
        var key = await keys.CurrentAsync(integrationEvent.EventId, cancellationToken);
        if (ticketedEvent is null || key is null)
        {
            throw new InvalidOperationException(
                $"Order {integrationEvent.OrderId} is for event {integrationEvent.EventId}, which Ticketing doesn't know yet; delivered again later.");
        }

        var labels = await events.LabelsAsync(
            integrationEvent.EventId,
            [.. integrationEvent.Seats.Select(seat => (seat.SeatId, seat.PriceCategoryId))],
            cancellationToken);

        var now = time.GetUtcNow();
        var issued = new List<Ticket>();
        foreach (var seat in integrationEvent.Seats)
        {
            var spec = new TicketSpec(
                Guid.CreateVersion7(),
                integrationEvent.OrderId,
                integrationEvent.EventId,
                seat.SeatId,
                integrationEvent.CustomerId,
                labels.GetValueOrDefault(seat.SeatId) ?? "General admission");

            var ticket = Ticket.Issue(spec, key.KeyId, signer.Sign(spec, key), now);
            tickets.Add(ticket);
            issued.Add(ticket);
        }

        publisher.Publish(new TicketsIssued(
            Guid.CreateVersion7(),
            now,
            integrationEvent.OrderId,
            integrationEvent.CustomerId,
            integrationEvent.CustomerEmail,
            integrationEvent.EventId,
            integrationEvent.EventName ?? ticketedEvent.Name,
            ticketedEvent.StartsAt,
            ticketedEvent.TimeZone,
            [.. issued.Select(ticket => new IssuedTicket(ticket.Id, ticket.SeatId, ticket.Label, ticket.Code))]));

        TicketingTelemetry.TicketsIssued.Add(issued.Count);
    }
}
