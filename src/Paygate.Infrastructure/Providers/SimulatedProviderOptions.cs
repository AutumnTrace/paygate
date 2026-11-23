using Paygate.Domain.Payments;

namespace Paygate.Infrastructure.Providers;

/// <summary>
/// One simulated PSP, bound from <c>Providers:{name}</c>. The simulators stand in for real PSP
/// integrations so routing, failover and webhooks can be exercised end to end with nothing to install.
/// </summary>
public sealed class SimulatedProviderOptions
{
    public const string SectionName = "Providers";

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Prefix of the provider references this simulator issues, e.g. <c>hrn</c>.</summary>
    public string ReferencePrefix { get; set; } = "sim";

    public IList<PaymentMethod> Methods { get; } = [];

    public IList<string> Currencies { get; } = [];

    /// <summary>Empty means every country.</summary>
    public IList<string> Countries { get; } = [];

    /// <summary>Methods that complete asynchronously: the provider answers Pending and confirms by webhook.</summary>
    public IList<PaymentMethod> AsyncMethods { get; } = [];

    public bool SupportsPayouts { get; set; }

    public bool SupportsRefunds { get; set; }

    public bool SupportsIdempotencyKeys { get; set; } = true;

    /// <summary>Initial fault settings. They can be changed at runtime through the admin API.</summary>
    public ProviderFaults Faults { get; set; } = new();
}

public enum OutageMode
{
    /// <summary>Healthy.</summary>
    None,

    /// <summary>Every call fails fast with a 503-style error.</summary>
    Errors,

    /// <summary>Every call hangs until the gateway's timeout gives up on it.</summary>
    Timeouts,
}

/// <summary>Fault injection knobs for one simulated provider.</summary>
public sealed record ProviderFaults
{
    /// <summary>Average response time. Each call varies by +/-20%.</summary>
    public int LatencyMs { get; init; } = 60;

    /// <summary>Share of calls (0 to 1) that fail with a transient error.</summary>
    public double ErrorRate { get; init; }

    public OutageMode Outage { get; init; } = OutageMode.None;
}
