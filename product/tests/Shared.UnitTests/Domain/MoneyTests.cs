using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.UnitTests.Domain;

public sealed class MoneyTests
{
    private static readonly CurrencyCode Usd = CurrencyCode.Create("USD").Value;

    [Fact]
    public void Amounts_in_the_same_currency_add_up()
    {
        var total = new Money(1250, CurrencyCode.Eur) + new Money(750, CurrencyCode.Eur);

        total.Should().Be(new Money(2000, CurrencyCode.Eur));
    }

    [Fact]
    public void Amounts_in_different_currencies_cannot_be_combined()
    {
        var add = () => new Money(100, CurrencyCode.Eur) + new Money(100, Usd);

        add.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Multiplying_by_a_quantity_keeps_the_currency()
    {
        new Money(1500, CurrencyCode.Eur).Multiply(4).Should().Be(new Money(6000, CurrencyCode.Eur));
    }

    [Fact]
    public void Overflow_throws_instead_of_wrapping()
    {
        var add = () => new Money(long.MaxValue, CurrencyCode.Eur) + new Money(1, CurrencyCode.Eur);

        add.Should().Throw<OverflowException>();
    }
}
