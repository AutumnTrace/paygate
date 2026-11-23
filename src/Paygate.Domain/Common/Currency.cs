using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Paygate.Domain.Common;

/// <summary>
/// An ISO-4217 currency and the number of digits in its minor unit
/// (2 for EUR cents, 0 for JPY, 3 for KWD fils).
/// </summary>
public sealed record Currency
{
    private static readonly FrozenDictionary<string, Currency> Supported = new Currency[]
    {
        new("EUR", 2), new("USD", 2), new("GBP", 2), new("CAD", 2), new("CHF", 2),
        new("SEK", 2), new("NOK", 2), new("DKK", 2), new("PLN", 2), new("BRL", 2),
        new("MXN", 2), new("JPY", 0), new("KWD", 3),
    }.ToFrozenDictionary(c => c.Code, StringComparer.Ordinal);

    private Currency(string code, int minorUnitDigits)
    {
        Code = code;
        MinorUnitDigits = minorUnitDigits;
    }

    /// <summary>Three-letter upper-case ISO-4217 code.</summary>
    public string Code { get; }

    public int MinorUnitDigits { get; }

    public static IEnumerable<Currency> All => Supported.Values.OrderBy(c => c.Code, StringComparer.Ordinal);

    public static Currency EUR => Supported["EUR"];

    public static Currency USD => Supported["USD"];

    public static Currency FromCode(string code) =>
        TryFromCode(code, out var currency)
            ? currency
            : throw new DomainException("unsupported_currency", $"Currency '{code}' is not a supported ISO-4217 code.");

    public static bool TryFromCode([NotNullWhen(true)] string? code, [NotNullWhen(true)] out Currency? currency)
    {
        currency = null;
        return code is { Length: 3 } && Supported.TryGetValue(code, out currency);
    }

    public override string ToString() => Code;
}
