using Paygate.Domain.Payments;

namespace Paygate.Application.Providers;

/// <summary>What a provider can process. The router never sends a payment to a provider that cannot take it.</summary>
public sealed record ProviderCapabilities
{
    public required IReadOnlySet<PaymentMethod> Methods { get; init; }

    /// <summary>ISO-4217 codes.</summary>
    public required IReadOnlySet<string> Currencies { get; init; }

    /// <summary>ISO 3166-1 alpha-2 codes. Empty means every country.</summary>
    public IReadOnlySet<string> Countries { get; init; } = new HashSet<string>();

    public bool SupportsPayouts { get; init; }

    public bool SupportsRefunds { get; init; }

    /// <summary>
    /// The provider deduplicates requests by an idempotency key. Only then is it safe to retry a call
    /// whose outcome is unknown (a timeout), because the provider will not charge twice.
    /// </summary>
    public bool SupportsIdempotencyKeys { get; init; }

    public bool Supports(PaymentKind kind, PaymentMethod method, string currency, string country) =>
        Methods.Contains(method)
        && Currencies.Contains(currency)
        && (Countries.Count == 0 || Countries.Contains(country))
        && (kind == PaymentKind.Deposit || SupportsPayouts);
}
