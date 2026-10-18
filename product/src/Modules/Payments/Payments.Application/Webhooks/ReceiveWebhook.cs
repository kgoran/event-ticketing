using EventTicketing.Payments.Contracts;
using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Payments.Application.Webhooks;

/// <summary>
/// A provider calls our webhook (ADR-0009): verify the signature, store the webhook once, answer
/// quickly. Processing happens afterwards in Workers, through a scheduled message due immediately.
/// </summary>
public sealed record ReceiveWebhook(string Provider, string Payload, IReadOnlyDictionary<string, string> Headers) : ICommand<Unit>;

/// <summary>Webhooks come from providers, not signed-in users: the signature, checked by the handler, is the authentication.</summary>
internal sealed class ReceiveWebhookAuthorizer : ICommandAuthorizer<ReceiveWebhook>
{
    public ValueTask<bool> IsAllowedAsync(ReceiveWebhook command, IExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}

internal sealed class ReceiveWebhookHandler(
    IEnumerable<IPaymentProvider> providers,
    IWebhookRepository webhooks,
    IMessageScheduler scheduler,
    TimeProvider time)
    : ICommandHandler<ReceiveWebhook, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ReceiveWebhook command, CancellationToken cancellationToken)
    {
        var provider = providers.SingleOrDefault(p => p.Name == command.Provider);
        if (provider is null)
        {
            return Error.NotFound("webhook.unknown_provider", "Unknown payment provider.");
        }

        var webhook = provider.VerifyWebhook(command.Payload, command.Headers);
        if (webhook is null)
        {
            return Error.Forbidden("webhook.invalid_signature", "The webhook signature is missing or invalid.");
        }

        // A provider retries until it gets a 2xx, so the same webhook can arrive more than once.
        if (await webhooks.ExistsAsync(provider.Name, webhook.EventId, cancellationToken))
        {
            return Unit.Value;
        }

        var now = time.GetUtcNow();
        var received = ReceivedWebhook.Receive(provider.Name, webhook.EventId, webhook.Type, webhook.ProviderPaymentId, command.Payload, now);
        webhooks.Add(received);
        scheduler.Schedule(new ProcessPaymentWebhook(received.Id), now);

        return Unit.Value;
    }
}

/// <summary>Processes a stored webhook in Workers.</summary>
public sealed record ProcessPaymentWebhook(Guid WebhookId);

internal sealed class ProcessPaymentWebhookHandler(
    IWebhookRepository webhooks,
    IPaymentAttemptRepository attempts,
    IIntegrationEventPublisher events,
    TimeProvider time)
    : IScheduledMessageHandler<ProcessPaymentWebhook>
{
    public async Task HandleAsync(ProcessPaymentWebhook message, CancellationToken cancellationToken)
    {
        var webhook = await webhooks.FindAsync(message.WebhookId, cancellationToken)
            ?? throw new InvalidOperationException($"Webhook {message.WebhookId} does not exist.");

        if (webhook.ProcessedAt is not null)
        {
            return;
        }

        // The session is stored before the fan ever sees the payment page, so a missing attempt is a
        // real problem: throwing makes the scheduler retry and eventually dead-letter it.
        var attempt = await attempts.FindByProviderPaymentAsync(webhook.Provider, webhook.ProviderPaymentId, cancellationToken)
            ?? throw new InvalidOperationException($"No payment attempt for {webhook.Provider} payment {webhook.ProviderPaymentId}.");

        var now = time.GetUtcNow();
        switch (webhook.Type)
        {
            case ProviderWebhookTypes.PaymentSucceeded when attempt.Succeed(now):
                events.Publish(new PaymentSucceeded(Guid.CreateVersion7(), now, attempt.OrderId, attempt.Id, attempt.AmountMinorUnits, attempt.Currency));
                break;
            case ProviderWebhookTypes.PaymentFailed:
                attempt.Fail(now);
                break;
        }

        webhook.MarkProcessed(now);
    }
}
