using EventTicketing.Payments.Application.Refunds;
using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Application.Scheduling;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Payments.Application.Webhooks;

/// <summary>
/// A provider calls our webhook (ADR-0009): verify the signature, store the webhook once, answer
/// quickly. Processing happens afterwards in Workers, through a scheduled message due immediately.
/// </summary>
public sealed record ReceiveWebhook(string Provider, string Payload, IReadOnlyDictionary<string, string> Headers) : ICommand<Unit>;

public static class WebhookErrors
{
    /// <summary>The webhook was stored before: the provider retried it or sent it twice. Answer 200.</summary>
    public static readonly Error Duplicate = Error.Conflict("webhook.duplicate", "This webhook was received before.");
}

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
            Record(provider.Name, "invalid_signature");
            return Error.Forbidden("webhook.invalid_signature", "The webhook signature is missing or invalid.");
        }

        // A provider retries until it gets a 2xx, so the same webhook can arrive more than once, even
        // at the same moment: then the unique index catches the second (PaymentsDbContext).
        if (await webhooks.ExistsAsync(provider.Name, webhook.EventId, cancellationToken))
        {
            Record(provider.Name, "duplicate");
            return WebhookErrors.Duplicate;
        }

        var now = time.GetUtcNow();
        var received = ReceivedWebhook.Receive(
            provider.Name,
            webhook.EventId,
            webhook.Type,
            webhook.ProviderPaymentId,
            webhook.Reference,
            webhook.AmountMinorUnits,
            webhook.Currency,
            webhook.ProviderRefundId,
            command.Payload,
            now);
        webhooks.Add(received);
        scheduler.Schedule(new ProcessPaymentWebhook(received.Id), now);

        Record(provider.Name, "stored");
        return Unit.Value;
    }

    private static void Record(string provider, string result) =>
        PaymentsTelemetry.Webhooks.Add(
            1,
            new KeyValuePair<string, object?>("eventticketing.payment.provider", provider),
            new KeyValuePair<string, object?>("eventticketing.webhook.result", result));
}

/// <summary>Processes a stored webhook in Workers.</summary>
public sealed record ProcessPaymentWebhook(Guid WebhookId);

internal sealed class ProcessPaymentWebhookHandler(
    IWebhookRepository webhooks,
    IPaymentAttemptRepository attempts,
    IRefundRepository refunds,
    PaymentOutcomes outcomes,
    RefundOutcomes refundOutcomes,
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

        if (webhook.Type == ProviderWebhookTypes.Ignored)
        {
            webhook.MarkProcessed(time.GetUtcNow());
            return;
        }

        var attempt = await FindAttemptAsync(webhook, cancellationToken);

        if (webhook.Type is ProviderWebhookTypes.RefundSucceeded or ProviderWebhookTypes.RefundFailed)
        {
            await ApplyRefundAsync(webhook, attempt, cancellationToken);
            webhook.MarkProcessed(time.GetUtcNow());
            return;
        }

        // A report about a different amount is not about our payment. Throwing retries it and then
        // dead-letters it, where someone looks at it; nothing is marked paid meanwhile.
        if (!attempt.Matches(webhook.AmountMinorUnits, webhook.Currency))
        {
            throw new InvalidOperationException(
                $"Webhook {webhook.ProviderEventId} reports {webhook.AmountMinorUnits} {webhook.Currency}; order {attempt.OrderId} expects {attempt.AmountMinorUnits} {attempt.Currency}.");
        }

        var status = webhook.Type switch
        {
            ProviderWebhookTypes.PaymentSucceeded => ProviderPaymentStatus.Succeeded,
            ProviderWebhookTypes.PaymentFailed => ProviderPaymentStatus.Failed,
            _ => (ProviderPaymentStatus?)null,
        };

        if (status is { } reported)
        {
            outcomes.Apply(attempt, reported);
        }

        webhook.MarkProcessed(time.GetUtcNow());
    }

    /// <summary>
    /// A refund webhook completes the payment's refund. It may overtake the provider's API answer,
    /// so the refund is found by its provider ID or, failing that, as the payment's refund.
    /// </summary>
    private async Task ApplyRefundAsync(ReceivedWebhook webhook, PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var refund = (webhook.ProviderRefundId is { } providerRefundId ? await refunds.FindByProviderRefundAsync(providerRefundId, cancellationToken) : null)
            ?? await refunds.FindByAttemptAsync(attempt.Id, cancellationToken)
            ?? throw new InvalidOperationException($"No refund for {webhook.Provider} payment {webhook.ProviderPaymentId}.");

        if (webhook.AmountMinorUnits != refund.AmountMinorUnits)
        {
            throw new InvalidOperationException(
                $"Webhook {webhook.ProviderEventId} reports a refund of {webhook.AmountMinorUnits}; refund {refund.Id} is for {refund.AmountMinorUnits}.");
        }

        refundOutcomes.Apply(refund, new RefundResult(
            webhook.Type == ProviderWebhookTypes.RefundSucceeded ? ProviderRefundStatus.Succeeded : ProviderRefundStatus.Failed,
            webhook.ProviderRefundId,
            webhook.Type == ProviderWebhookTypes.RefundFailed ? "The provider reported the refund failed." : null));
    }

    /// <summary>
    /// By the provider's payment ID, or, if the webhook overtook the call that stores the session,
    /// by our reference (the order ID). A missing attempt is a real problem: throwing makes the
    /// scheduler retry and eventually dead-letter it.
    /// </summary>
    private async Task<PaymentAttempt> FindAttemptAsync(ReceivedWebhook webhook, CancellationToken cancellationToken)
    {
        if (await attempts.FindByProviderPaymentAsync(webhook.Provider, webhook.ProviderPaymentId, cancellationToken) is { } attempt)
        {
            return attempt;
        }

        if (Guid.TryParse(webhook.Reference, out var orderId)
            && await attempts.FindByOrderAsync(orderId, cancellationToken) is { ProviderPaymentId: null } beforeSession
            && beforeSession.Provider == webhook.Provider)
        {
            return beforeSession;
        }

        throw new InvalidOperationException($"No payment attempt for {webhook.Provider} payment {webhook.ProviderPaymentId}.");
    }
}
