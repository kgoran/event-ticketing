using Microsoft.Extensions.Options;

namespace EventTicketing.Tooling.LocalPay;

public sealed class HeadlessOptions
{
    /// <summary>Pays every new payment automatically, for load tests: no browser involved.</summary>
    public bool Enabled { get; set; }

    public TimeSpan MinDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The fan in headless mode: approves each new payment after a random delay, as if someone clicked
/// Approve. Scenarios still apply (an amount ending in .01 is declined).
/// </summary>
public sealed class HeadlessPayer(
    PaymentStore store,
    Webhooks webhooks,
    IOptions<HeadlessOptions> options,
    TimeProvider time,
    IHostApplicationLifetime lifetime)
{
    public bool Enabled => options.Value.Enabled;

    public void PayLater(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (!Enabled)
        {
            return;
        }

        var (min, max) = (options.Value.MinDelay, options.Value.MaxDelay);
        var delay = min + ((max - min) * Random.Shared.NextDouble());
        _ = PayAsync(payment.Id, delay, lifetime.ApplicationStopping);
    }

    private async Task PayAsync(string paymentId, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, time, cancellationToken);
            var (payment, change) = store.Complete(paymentId, approved: true);
            if (payment is not null && change == Change.Changed)
            {
                webhooks.PaymentChanged(payment);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
