using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Paygate.Application.Providers;

/// <summary>All registered providers, by name.</summary>
public sealed class ProviderCatalog
{
    private readonly FrozenDictionary<string, IPaymentProvider> _byName;

    public ProviderCatalog(IEnumerable<IPaymentProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _byName = providers.ToFrozenDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IPaymentProvider> All => _byName.Values;

    public IEnumerable<string> Names => _byName.Keys;

    public bool TryGet(string name, [NotNullWhen(true)] out IPaymentProvider? provider) =>
        _byName.TryGetValue(name, out provider);

    public IPaymentProvider Get(string name) =>
        TryGet(name, out var provider) ? provider : throw new KeyNotFoundException($"Unknown provider '{name}'.");
}
