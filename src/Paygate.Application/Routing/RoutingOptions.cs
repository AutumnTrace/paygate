using Paygate.Domain.Payments;

namespace Paygate.Application.Routing;

/// <summary>
/// Routing table, bound from the <c>Routing</c> configuration section.
/// Rules are evaluated top to bottom and the <b>first matching rule wins</b>, like firewall rules,
/// so the table reads the same way it behaves.
/// </summary>
public sealed class RoutingOptions
{
    public const string SectionName = "Routing";

    public IList<RoutingRule> Rules { get; } = [];
}

public sealed class RoutingRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Empty matches every kind.</summary>
    public IList<PaymentKind> Kinds { get; } = [];

    /// <summary>Empty matches every method.</summary>
    public IList<PaymentMethod> Methods { get; } = [];

    /// <summary>ISO-4217 codes. Empty matches every currency.</summary>
    public IList<string> Currencies { get; } = [];

    /// <summary>ISO 3166-1 alpha-2 codes. Empty matches every country.</summary>
    public IList<string> Countries { get; } = [];

    /// <summary>
    /// Inclusive lower bound in minor units of the payment's own currency.
    /// Minor units are not comparable across currencies, so pair amount bands with a currency filter.
    /// </summary>
    public long? MinAmount { get; set; }

    /// <summary>Exclusive upper bound in minor units of the payment's own currency.</summary>
    public long? MaxAmount { get; set; }

    /// <summary>Providers to try, ordered by priority (1 first). Equal priorities split traffic by weight.</summary>
    public IList<RouteTarget> Targets { get; } = [];

    public bool Matches(RoutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return (Kinds.Count == 0 || Kinds.Contains(context.Kind))
            && (Methods.Count == 0 || Methods.Contains(context.Method))
            && (Currencies.Count == 0 || Currencies.Contains(context.Amount.Currency.Code, StringComparer.Ordinal))
            && (Countries.Count == 0 || Countries.Contains(context.Country, StringComparer.Ordinal))
            && (MinAmount is null || context.Amount.MinorUnits >= MinAmount)
            && (MaxAmount is null || context.Amount.MinorUnits < MaxAmount);
    }
}

public sealed class RouteTarget
{
    public string Provider { get; set; } = string.Empty;

    /// <summary>1 is tried first. Lower priorities are failover targets.</summary>
    public int Priority { get; set; } = 1;

    /// <summary>Relative share of traffic among targets with the same priority.</summary>
    public int Weight { get; set; } = 100;
}
