namespace Paygate.Application.Resilience;

/// <summary>
/// Resilience settings applied to every provider pipeline, bound from the <c>Resilience</c> section.
/// The defaults favour failing over quickly: a player waiting on a deposit button will not wait 30 seconds.
/// </summary>
public sealed class ProviderResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>Upper bound for one provider call.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for all attempts against one provider, retries included.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Retries per provider. Only used when the provider supports idempotency keys.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Share of failed calls in the sampling window that opens the circuit.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Calls needed in the sampling window before the breaker may open.</summary>
    public int MinimumThroughput { get; set; } = 3;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an open circuit stays open before one trial call is let through (half-open).</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
