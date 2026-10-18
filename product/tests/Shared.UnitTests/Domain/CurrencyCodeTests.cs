using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.UnitTests.Domain;

public sealed class CurrencyCodeTests
{
    [Fact]
    public void Accepts_three_upper_case_letters()
    {
        var result = CurrencyCode.Create("EUR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(CurrencyCode.Eur);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eur")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    public void Rejects_anything_else(string? code)
    {
        var result = CurrencyCode.Create(code);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("currency.invalid");
    }
}
