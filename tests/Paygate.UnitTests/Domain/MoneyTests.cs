using FsCheck.Xunit;
using Paygate.Domain.Common;

namespace Paygate.UnitTests.Domain;

public sealed class MoneyTests
{
    [Theory]
    [InlineData(1250, "EUR", "12.50 EUR")]
    [InlineData(5, "USD", "0.05 USD")]
    [InlineData(1500, "JPY", "1500 JPY")]
    [InlineData(1234, "KWD", "1.234 KWD")]
    public void Formats_with_the_currency_minor_unit(long minor, string currency, string expected) =>
        Assert.Equal(expected, Money.Of(minor, currency).ToString());

    [Fact]
    public void Major_units_are_exact_decimals() =>
        Assert.Equal(0.1m, Money.Of(10, "EUR").ToMajorUnits());

    [Fact]
    public void Amounts_in_different_currencies_cannot_be_added()
    {
        var ex = Assert.Throws<DomainException>(() => Money.Of(100, "EUR") + Money.Of(100, "USD"));
        Assert.Equal("currency_mismatch", ex.Code);
    }

    [Fact]
    public void Amounts_in_different_currencies_cannot_be_compared() =>
        Assert.Throws<DomainException>(() => Money.Of(100, "EUR") > Money.Of(50, "SEK"));

    [Fact]
    public void Equality_is_by_value() =>
        Assert.Equal(Money.Of(100, "EUR"), new Money(100, Currency.EUR));

    [Theory]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("XXX")]
    [InlineData("")]
    public void Unknown_or_malformed_currency_codes_are_rejected(string code)
    {
        Assert.False(Currency.TryFromCode(code, out _));
        Assert.Equal("unsupported_currency", Assert.Throws<DomainException>(() => Currency.FromCode(code)).Code);
    }

    [Fact]
    public void Overflow_is_detected_instead_of_wrapping() =>
        Assert.Throws<OverflowException>(() => Money.Of(long.MaxValue, "EUR") + Money.Of(1, "EUR"));

    [Property]
    public bool Subtraction_undoes_addition(int a, int b) =>
        Money.Of(a, "EUR") + Money.Of(b, "EUR") - Money.Of(b, "EUR") == Money.Of(a, "EUR");

    [Property]
    public bool Addition_is_commutative(int a, int b) =>
        Money.Of(a, "GBP") + Money.Of(b, "GBP") == Money.Of(b, "GBP") + Money.Of(a, "GBP");

    [Property]
    public bool Ordering_matches_minor_units(int a, int b) =>
        Money.Of(a, "SEK") < Money.Of(b, "SEK") == a < b;
}
