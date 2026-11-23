using System.Collections.Concurrent;
using Polly.CircuitBreaker;

namespace Paygate.Application.Resilience;

/// <summary>Live health of one provider: its circuit breaker plus a rolling window of recent calls.</summary>
public sealed class ProviderHealth(string provider)
{
    private const int WindowSize = 50;

    private readonly Lock _gate = new();
    private readonly Queue<(bool Success, double LatencyMs)> _window = new();
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;
    private DateTimeOffset? _circuitOpenedAt;
    private long _totalCalls;

    public string Provider { get; } = provider;

    /// <summary>Wired into the provider's Polly circuit breaker; reads its state without side effects.</summary>
    public CircuitBreakerStateProvider Circuit { get; } = new();

    /// <summary>Lets an operator isolate (force open) or close the circuit by hand.</summary>
    public CircuitBreakerManualControl CircuitControl { get; } = new();

    public void RecordSuccess(TimeSpan latency) => Record(true, latency, error: null, at: null);

    public void RecordFailure(TimeSpan latency, string error, DateTimeOffset at) => Record(false, latency, error, at);

    public void RecordCircuitOpened(DateTimeOffset at)
    {
        lock (_gate)
        {
            _circuitOpenedAt = at;
        }
    }

    public ProviderHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            var count = _window.Count;
            var successes = _window.Count(x => x.Success);
            var state = Circuit.CircuitState;
            return new ProviderHealthSnapshot(
                Provider,
                state,
                state is CircuitState.Open or CircuitState.Isolated ? _circuitOpenedAt : null,
                count == 0 ? null : Math.Round((double)successes / count, 3),
                count == 0 ? null : Math.Round(_window.Average(x => x.LatencyMs), 1),
                _totalCalls,
                _lastError,
                _lastErrorAt);
        }
    }

    private void Record(bool success, TimeSpan latency, string? error, DateTimeOffset? at)
    {
        lock (_gate)
        {
            _totalCalls++;
            _window.Enqueue((success, latency.TotalMilliseconds));
            if (_window.Count > WindowSize)
            {
                _window.Dequeue();
            }

            if (!success)
            {
                _lastError = error;
                _lastErrorAt = at;
            }
        }
    }
}

public sealed record ProviderHealthSnapshot(
    string Provider,
    CircuitState CircuitState,
    DateTimeOffset? CircuitOpenedAt,
    double? SuccessRate,
    double? AverageLatencyMs,
    long TotalCalls,
    string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>One <see cref="ProviderHealth"/> per provider, shared by the router, metrics and the API.</summary>
public sealed class ProviderHealthRegistry
{
    private readonly ConcurrentDictionary<string, ProviderHealth> _health = new(StringComparer.OrdinalIgnoreCase);

    public ProviderHealth For(string provider) => _health.GetOrAdd(provider, static name => new ProviderHealth(name));

    public IReadOnlyCollection<ProviderHealth> All => _health.Values.OrderBy(h => h.Provider, StringComparer.Ordinal).ToList();
}
