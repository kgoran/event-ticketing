using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;

namespace EventTicketing.Payments.Application.Checkout;

/// <summary>
/// Creates the payment session for an order (ADR-0009, step 3). A coordinator: it records the
/// attempt in one transaction, calls the provider outside any transaction, and stores the session in
/// another. Repeating it returns the same session. Called through IPaymentsApi only.
/// </summary>
public sealed record CreateCheckout(Guid OrderId, long AmountMinorUnits, string Currency, string ReturnUrl)
    : ICommand<string>, INonTransactionalCommand;

internal sealed class CreateCheckoutAuthorizer : InternalCommandAuthorizer<CreateCheckout>;

internal sealed class CreateCheckoutHandler(
    ICommandHandler<RecordPaymentAttempt, RecordedAttempt> recordAttempt,
    ICommandHandler<StorePaymentSession, Unit> storeSession,
    IEnumerable<IPaymentProvider> providers,
    PaymentsPolicy policy,
    TimeProvider time)
    : ICommandHandler<CreateCheckout, string>
{
    public async Task<Result<string>> HandleAsync(CreateCheckout command, CancellationToken cancellationToken)
    {
        var recorded = await recordAttempt.HandleAsync(
            new RecordPaymentAttempt(command.OrderId, command.AmountMinorUnits, command.Currency, policy.Provider),
            cancellationToken);

        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (recorded.Value.CheckoutUrl is { } existingUrl)
        {
            return existingUrl;
        }

        var provider = providers.Single(p => p.Name == recorded.Value.Provider);
        CreatePaymentResult created;

        try
        {
            created = await provider.CreatePaymentAsync(
                new CreatePaymentRequest(command.OrderId, command.AmountMinorUnits, command.Currency, command.ReturnUrl, time.GetUtcNow() + policy.SessionLifetime),
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return CheckoutErrors.ProviderUnavailable;
        }

        var stored = await storeSession.HandleAsync(
            new StorePaymentSession(recorded.Value.AttemptId, created.ProviderPaymentId, created.CheckoutUrl),
            cancellationToken);

        return stored.IsSuccess ? created.CheckoutUrl : stored.Error;
    }
}

public static class CheckoutErrors
{
    public static readonly Error ProviderUnavailable =
        new("payments.provider_unavailable", "The payment provider is unavailable. Please try again.");
}

/// <summary>Records the order's payment attempt, or returns the existing one.</summary>
public sealed record RecordPaymentAttempt(Guid OrderId, long AmountMinorUnits, string Currency, string Provider) : ICommand<RecordedAttempt>;

public sealed record RecordedAttempt(Guid AttemptId, string Provider, string? CheckoutUrl);

internal sealed class RecordPaymentAttemptAuthorizer : InternalCommandAuthorizer<RecordPaymentAttempt>;

internal sealed class RecordPaymentAttemptHandler(IPaymentAttemptRepository attempts, TimeProvider time)
    : ICommandHandler<RecordPaymentAttempt, RecordedAttempt>
{
    public async Task<Result<RecordedAttempt>> HandleAsync(RecordPaymentAttempt command, CancellationToken cancellationToken)
    {
        var attempt = await attempts.FindByOrderAsync(command.OrderId, cancellationToken);
        if (attempt is null)
        {
            attempt = PaymentAttempt.Create(command.OrderId, command.AmountMinorUnits, command.Currency, command.Provider, time.GetUtcNow());
            attempts.Add(attempt);
        }

        return new RecordedAttempt(attempt.Id, attempt.Provider, attempt.CheckoutUrl);
    }
}

/// <summary>Stores the session the provider created for an attempt.</summary>
public sealed record StorePaymentSession(Guid AttemptId, string ProviderPaymentId, string CheckoutUrl) : ICommand<Unit>;

internal sealed class StorePaymentSessionAuthorizer : InternalCommandAuthorizer<StorePaymentSession>;

internal sealed class StorePaymentSessionHandler(IPaymentAttemptRepository attempts) : ICommandHandler<StorePaymentSession, Unit>
{
    public async Task<Result<Unit>> HandleAsync(StorePaymentSession command, CancellationToken cancellationToken)
    {
        var attempt = await attempts.FindAsync(command.AttemptId, cancellationToken);
        if (attempt is null)
        {
            return Error.NotFound("payments.attempt_not_found", "The payment attempt does not exist.");
        }

        attempt.SessionCreated(command.ProviderPaymentId, command.CheckoutUrl);
        return Unit.Value;
    }
}
