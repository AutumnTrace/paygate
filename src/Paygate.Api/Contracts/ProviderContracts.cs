using Paygate.Application.Providers;
using Paygate.Application.Resilience;
using Paygate.Domain.Payments;
using Paygate.Infrastructure.Providers;
using Polly.CircuitBreaker;

namespace Paygate.Api.Contracts;

public sealed record ProviderStatusResponse(
    string Name,
    string DisplayName,
    IReadOnlyList<PaymentMethod> Methods,
    IReadOnlyList<string> Currencies,
    IReadOnlyList<string> Countries,
    bool SupportsPayouts,
    bool SupportsRefunds,
    bool SupportsIdempotencyKeys,
    CircuitResponse Circuit,
    ProviderHealthResponse Health,
    ProviderFaults? Faults)
{
    public static ProviderStatusResponse From(IPaymentProvider provider, ProviderHealthSnapshot health, ProviderFaults? faults)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(health);

        var capabilities = provider.Capabilities;
        return new ProviderStatusResponse(
            provider.Name,
            provider.DisplayName,
            capabilities.Methods.Order().ToList(),
            capabilities.Currencies.Order(StringComparer.Ordinal).ToList(),
            capabilities.Countries.Order(StringComparer.Ordinal).ToList(),
            capabilities.SupportsPayouts,
            capabilities.SupportsRefunds,
            capabilities.SupportsIdempotencyKeys,
            new CircuitResponse(health.CircuitState, health.CircuitOpenedAt),
            new ProviderHealthResponse(health.SuccessRate, health.AverageLatencyMs, health.TotalCalls, health.LastError, health.LastErrorAt),
            faults);
    }
}

/// <param name="State">closed, half_open, open or isolated.</param>
/// <param name="OpenedAt">When the circuit last opened, while it is open.</param>
public sealed record CircuitResponse(CircuitState State, DateTimeOffset? OpenedAt);

/// <param name="SuccessRate">Share of the last 50 calls that got an answer (declines count as answers).</param>
public sealed record ProviderHealthResponse(double? SuccessRate, double? AverageLatencyMs, long TotalCalls, string? LastError, DateTimeOffset? LastErrorAt);

/// <summary>Changes the simulated provider's behaviour. Omitted fields keep their current value.</summary>
/// <param name="LatencyMs">Average response time, 0 to 60000.</param>
/// <param name="ErrorRate">Share of calls that fail with a transient error, 0 to 1.</param>
/// <param name="Outage">none, errors (fail fast) or timeouts (hang until the gateway gives up).</param>
public sealed record UpdateFaultsRequest(int? LatencyMs, double? ErrorRate, OutageMode? Outage);
