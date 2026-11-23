using System.Diagnostics;
using System.Diagnostics.Metrics;
using Paygate.Application.Resilience;
using Polly.CircuitBreaker;

namespace Paygate.Application.Observability;

/// <summary>Names shared by tracing and metrics so exporters can find them.</summary>
public static class PaygateTelemetry
{
    public const string Name = "Paygate";

    public static readonly ActivitySource ActivitySource = new(Name);
}

/// <summary>
/// Business and resilience metrics. Exported through OpenTelemetry (OTLP, Azure Monitor)
/// next to the built-in ASP.NET Core and Polly (<c>resilience.polly.*</c>) instruments.
/// </summary>
public sealed class PaygateMetrics
{
    private readonly Counter<long> _payments;
    private readonly Counter<long> _failovers;
    private readonly Counter<long> _circuitTransitions;
    private readonly Histogram<double> _providerDuration;
    private readonly Counter<long> _webhooks;
    private readonly Counter<long> _idempotency;

    public PaygateMetrics(IMeterFactory meterFactory, ProviderHealthRegistry health)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(health);

        var meter = meterFactory.Create(PaygateTelemetry.Name);

        _payments = meter.CreateCounter<long>(
            "paygate.payments", "{payment}", "Payments that finished routing, by kind, method, provider and resulting status.");

        _failovers = meter.CreateCounter<long>(
            "paygate.routing.failovers", "{failover}", "Times the router moved past a provider to the next candidate, by reason.");

        _circuitTransitions = meter.CreateCounter<long>(
            "paygate.provider.circuit_transitions", "{transition}", "Circuit breaker state changes per provider.");

        _providerDuration = meter.CreateHistogram(
            "paygate.provider.duration",
            "s",
            "Duration of calls to a provider, including retries, by operation and outcome.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10] });

        _webhooks = meter.CreateCounter<long>(
            "paygate.webhooks", "{webhook}", "Provider webhooks received, by provider and outcome.");

        _idempotency = meter.CreateCounter<long>(
            "paygate.idempotency.requests", "{request}", "Requests carrying an Idempotency-Key, by outcome (new, replayed, in_flight, mismatch).");

        meter.CreateObservableGauge(
            "paygate.provider.circuit_state",
            () => health.All.Select(h => new Measurement<int>(
                ToGaugeValue(h.Circuit.CircuitState),
                new KeyValuePair<string, object?>("provider", h.Provider))),
            "{state}",
            "Circuit state per provider: 0 closed, 1 half-open, 2 open, 3 isolated.");
    }

    public void PaymentRouted(string kind, string method, string? provider, string status) =>
        _payments.Add(1, new TagList
        {
            { "kind", kind },
            { "method", method },
            { "provider", provider ?? "none" },
            { "status", status },
        });

    public void Failover(string fromProvider, string reason, string kind) =>
        _failovers.Add(1, new TagList { { "from_provider", fromProvider }, { "reason", reason }, { "kind", kind } });

    public void CircuitTransition(string provider, string state) =>
        _circuitTransitions.Add(1, new TagList { { "provider", provider }, { "state", state } });

    public void ProviderCall(string provider, string operation, string outcome, TimeSpan duration) =>
        _providerDuration.Record(duration.TotalSeconds, new TagList
        {
            { "provider", provider },
            { "operation", operation },
            { "outcome", outcome },
        });

    public void Webhook(string provider, string outcome) =>
        _webhooks.Add(1, new TagList { { "provider", provider }, { "outcome", outcome } });

    public void IdempotentRequest(string outcome) =>
        _idempotency.Add(1, new TagList { { "outcome", outcome } });

    internal static int ToGaugeValue(CircuitState state) => state switch
    {
        CircuitState.Closed => 0,
        CircuitState.HalfOpen => 1,
        CircuitState.Open => 2,
        CircuitState.Isolated => 3,
        _ => -1,
    };
}
