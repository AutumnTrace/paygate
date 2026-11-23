using System.Globalization;

namespace Paygate.Domain.Common;

/// <summary>
/// An amount of money in integer minor units (cents) with an explicit currency.
/// Money is never a floating-point number, and two amounts in different currencies
/// can never be added or compared.
/// </summary>
public sealed record Money : IComparable<Money>
{
    public Money(long minorUnits, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        MinorUnits = minorUnits;
        Currency = currency;
    }

    public long MinorUnits { get; }

    public Currency Currency { get; }

    public bool IsZero => MinorUnits == 0;

    public bool IsPositive => MinorUnits > 0;

    public static Money Of(long minorUnits, string currencyCode) => new(minorUnits, Currency.FromCode(currencyCode));

    public static Money Zero(Currency currency) => new(0, currency);

    /// <summary>The amount in major units, e.g. 12.50 for 1250 EUR cents. Exact, because it is a decimal.</summary>
    public decimal ToMajorUnits()
    {
        var scale = 1m;
        for (var i = 0; i < Currency.MinorUnitDigits; i++)
        {
            scale *= 10;
        }

        return MinorUnits / scale;
    }

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

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money? other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameCurrency(this, other);
        return MinorUnits.CompareTo(other.MinorUnits);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{ToMajorUnits().ToString("F" + Currency.MinorUnitDigits, CultureInfo.InvariantCulture)} {Currency.Code}");

    private static void EnsureSameCurrency(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Currency != right.Currency)
        {
            throw new DomainException(
                "currency_mismatch",
                $"Cannot combine amounts in {left.Currency.Code} and {right.Currency.Code}.");
        }
    }
}
