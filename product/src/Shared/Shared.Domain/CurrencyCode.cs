namespace EventTicketing.Shared.Domain;

/// <summary>An ISO 4217 currency code, e.g. EUR. One currency per event (ADR-0024).</summary>
public sealed record CurrencyCode
{
    private CurrencyCode(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static CurrencyCode Eur { get; } = new("EUR");

    public static Result<CurrencyCode> Create(string? code) =>
        code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z')
            ? new CurrencyCode(code)
            : Error.Validation("currency.invalid", "A currency code is three upper-case letters (ISO 4217).");

    public override string ToString() => Value;
}
