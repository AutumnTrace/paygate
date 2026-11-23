using System.Collections.Concurrent;

namespace Paygate.Infrastructure.Providers;

/// <summary>
/// Current fault settings of every simulated provider. Seeded from configuration and changed at
/// runtime by <c>PUT /v1/admin/providers/{name}/faults</c>, which is how failover is demonstrated live.
/// </summary>
public sealed class ProviderFaultRegistry
{
    private readonly ConcurrentDictionary<string, ProviderFaults> _faults = new(StringComparer.OrdinalIgnoreCase);

    public ProviderFaults Get(string provider) => _faults.GetValueOrDefault(provider) ?? new ProviderFaults();

    public void Set(string provider, ProviderFaults faults)
    {
        ArgumentNullException.ThrowIfNull(faults);
        if (faults.ErrorRate is < 0 or > 1 || faults.LatencyMs is < 0 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(faults), "ErrorRate must be within [0, 1] and LatencyMs within [0, 60000].");
        }

        _faults[provider] = faults;
    }
}
