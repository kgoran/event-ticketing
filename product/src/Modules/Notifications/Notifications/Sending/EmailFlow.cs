using System.Diagnostics.Metrics;
using System.Text.Json;
using EventTicketing.Notifications.Email;
using EventTicketing.Notifications.Persistence;
using EventTicketing.Orders.Contracts;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Domain;
using EventTicketing.Ticketing.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Notifications.Sending;

/// <summary>Send the email of a delivery. Scheduled by the consumer that recorded it, in the same transaction.</summary>
/// <param name="OrderId">What the email is about; also lets a test or an operator find the message.</param>
public sealed record SendEmail(Guid DeliveryId, Guid OrderId);

/// <summary>The fan's tickets, by email, once Ticketing has issued them.</summary>
internal sealed partial class QueueTicketsEmailOnTicketsIssued(
    NotificationsDbContext db,
    IMessageScheduler scheduler,
    TimeProvider time,
    ILogger<QueueTicketsEmailOnTicketsIssued> logger)
    : IIntegrationEventHandler<TicketsIssued>
{
    public async Task HandleAsync(TicketsIssued integrationEvent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(integrationEvent.CustomerEmail))
        {
            // The token had no email; the fan still has the tickets under "My tickets".
            LogNoAddress(logger, integrationEvent.OrderId);
            NotificationsTelemetry.Emails.Add(1, NotificationsTelemetry.Tags(EmailKinds.Tickets, "no_address"));
            return;
        }

        var model = new TicketsEmailModel(
            integrationEvent.OrderId,
            integrationEvent.EventName,
            integrationEvent.StartsAt,
            integrationEvent.TimeZone,
            [.. integrationEvent.Tickets.Select(ticket => new TicketLine(ticket.TicketId, ticket.Label, ticket.Code))]);

        await EmailQueue.QueueAsync(db, scheduler, time, EmailKinds.Tickets, integrationEvent.OrderId, integrationEvent.CustomerEmail, model, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tickets for order {OrderId} not emailed: the buyer's token had no email")]
    private static partial void LogNoAddress(ILogger logger, Guid orderId);
}

/// <summary>Tells the fan their money is on its way back, and why.</summary>
internal sealed class QueueRefundEmailOnOrderRefunded(NotificationsDbContext db, IMessageScheduler scheduler, TimeProvider time)
    : IIntegrationEventHandler<OrderRefunded>
{
    public async Task HandleAsync(OrderRefunded integrationEvent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(integrationEvent.CustomerEmail))
        {
            NotificationsTelemetry.Emails.Add(1, NotificationsTelemetry.Tags(EmailKinds.Refund, "no_address"));
            return;
        }

        var model = new RefundEmailModel(
            integrationEvent.OrderId,
            integrationEvent.EventName ?? "your event",
            integrationEvent.AmountMinorUnits,
            integrationEvent.Currency,
            integrationEvent.Reason);

        await EmailQueue.QueueAsync(db, scheduler, time, EmailKinds.Refund, integrationEvent.OrderId, integrationEvent.CustomerEmail, model, cancellationToken);
    }
}

internal static class EmailQueue
{
    /// <summary>
    /// Records the delivery and schedules its sending, in the consumer's transaction: both exist only
    /// if it commits. One email of a kind per order, whatever arrives how often.
    /// </summary>
    public static async Task QueueAsync<TModel>(
        NotificationsDbContext db,
        IMessageScheduler scheduler,
        TimeProvider time,
        string kind,
        Guid orderId,
        string recipient,
        TModel model,
        CancellationToken cancellationToken)
    {
        if (await db.Deliveries.AnyAsync(delivery => delivery.Kind == kind && delivery.OrderId == orderId, cancellationToken))
        {
            return;
        }

        var now = time.GetUtcNow();
        var delivery = Delivery.Queue(kind, orderId, recipient, JsonSerializer.Serialize(model, JsonSerializerOptions.Web), now);
        db.Deliveries.Add(delivery);
        scheduler.Schedule(new SendEmail(delivery.Id, orderId), now);
    }
}

/// <summary>
/// Sends a delivery's email outside any transaction (SMTP isn't part of the database), then marks it
/// sent with a command of its own (ADR-0052). If sending fails, the message is retried with backoff;
/// if the process dies after sending but before marking, the retry sends a second copy.
/// </summary>
internal sealed class SendEmailHandler(
    NotificationsDbContext db,
    EmailComposer composer,
    IEmailSender sender,
    ICommandHandler<MarkDeliverySent, Unit> markSent)
    : IScheduledMessageHandler<SendEmail>, INonTransactionalMessageHandler
{
    public async Task HandleAsync(SendEmail message, CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == message.DeliveryId, cancellationToken)
            ?? throw new InvalidOperationException($"Delivery {message.DeliveryId} does not exist.");

        if (delivery.Status == DeliveryStatus.Sent)
        {
            return;
        }

        await sender.SendAsync(await composer.ComposeAsync(delivery), cancellationToken);
        NotificationsTelemetry.Emails.Add(1, NotificationsTelemetry.Tags(delivery.Kind, "sent"));

        var marked = await markSent.HandleAsync(new MarkDeliverySent(delivery.Id), cancellationToken);
        if (marked.IsFailure)
        {
            throw new InvalidOperationException($"Marking delivery {delivery.Id} sent failed: {marked.Error.Code}.");
        }
    }
}

public sealed record MarkDeliverySent(Guid DeliveryId) : ICommand<Unit>;

internal sealed class MarkDeliverySentAuthorizer : InternalCommandAuthorizer<MarkDeliverySent>;

internal sealed class MarkDeliverySentHandler(NotificationsDbContext db, TimeProvider time) : ICommandHandler<MarkDeliverySent, Unit>
{
    public async Task<Result<Unit>> HandleAsync(MarkDeliverySent command, CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries.SingleAsync(d => d.Id == command.DeliveryId, cancellationToken);
        delivery.MarkSent(time.GetUtcNow());
        return Unit.Value;
    }
}

/// <summary>Notifications' metrics (docs/observability.md).</summary>
public static class NotificationsTelemetry
{
    public const string Name = "EventTicketing.Notifications";

    private static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> Emails = Meter.CreateCounter<long>(
        "eventticketing.notifications.emails",
        description: "Emails by kind (tickets, refund) and result (sent; no_address when the buyer's token had none).");

    internal static KeyValuePair<string, object?>[] Tags(string kind, string result) =>
        [new("email.kind", kind), new("email.result", result)];
}
