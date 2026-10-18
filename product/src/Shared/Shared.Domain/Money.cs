namespace EventTicketing.Shared.Domain;

/// <summary>
/// An amount in integer minor units (cents for EUR) plus its currency. Arithmetic is allowed only
/// within one currency, and overflow throws instead of wrapping (ADR-0032).
/// </summary>
public sealed record Money
{
    public Money(long minorUnits, CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        MinorUnits = minorUnits;
        Currency = currency;
    }

    public long MinorUnits { get; }

    public CurrencyCode Currency { get; }

    public static Money Zero(CurrencyCode currency) => new(0, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.MinorUnits + right.MinorUnits), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.MinorUnits - right.MinorUnits), left.Currency);
    }

    public Money Multiply(int quantity) => new(checked(MinorUnits * quantity), Currency);

    public override string ToString() => $"{MinorUnits} {Currency} (minor units)";

    private static void EnsureSameCurrency(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException($"Can't combine amounts in {left.Currency} and {right.Currency}.");
        }
    }
}
