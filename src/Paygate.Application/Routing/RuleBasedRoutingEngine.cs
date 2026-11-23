using Microsoft.Extensions.Options;
using Paygate.Application.Providers;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.Application.Routing;

public sealed record RoutingContext(PaymentKind Kind, PaymentMethod Method, Money Amount, string Country);

public sealed record RouteCandidate(string Provider, int Priority);

/// <summary>The ordered list of providers to try for one payment, and the rule that produced it.</summary>
public sealed record RoutePlan(string? Rule, IReadOnlyList<RouteCandidate> Candidates)
{
    public static readonly RoutePlan None = new(null, []);

    public bool IsEmpty => Candidates.Count == 0;
}

public interface IRoutingEngine
{
    RoutePlan Plan(RoutingContext context);
}

/// <summary>
/// Turns the routing table into an ordered candidate list:
/// first matching rule, then priority groups in order, then a weighted shuffle inside each group,
/// and finally a capability filter so a provider never sees a payment it cannot process.
/// Health is deliberately not considered here; the router skips open circuits at call time,
/// so a plan is cheap, pure and easy to test.
/// </summary>
public sealed class RuleBasedRoutingEngine(IOptionsMonitor<RoutingOptions> options, ProviderCatalog catalog, Random random) : IRoutingEngine
{
    public RoutePlan Plan(RoutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rule = options.CurrentValue.Rules.FirstOrDefault(r => r.Matches(context));
        if (rule is null)
        {
            return RoutePlan.None;
        }

        var candidates = new List<RouteCandidate>(rule.Targets.Count);
        foreach (var group in rule.Targets.GroupBy(t => t.Priority).OrderBy(g => g.Key))
        {
            foreach (var target in WeightedOrder(group))
            {
                if (catalog.TryGet(target.Provider, out var provider)
                    && provider.Capabilities.Supports(context.Kind, context.Method, context.Amount.Currency.Code, context.Country)
                    && candidates.TrueForAll(c => c.Provider != provider.Name))
                {
                    candidates.Add(new RouteCandidate(provider.Name, target.Priority));
                }
            }
        }

        return new RoutePlan(rule.Name, candidates);
    }

    /// <summary>
    /// Weighted random order without replacement (Efraimidis-Spirakis): each target draws
    /// <c>u^(1/weight)</c> and the highest key goes first. A target with weight 80 leads
    /// about 80% of the time against one with weight 20, and the other is still a failover option.
    /// </summary>
    private IEnumerable<RouteTarget> WeightedOrder(IEnumerable<RouteTarget> group)
    {
        var targets = group.ToList();
        if (targets.Count == 1)
        {
            return targets;
        }

        return targets
            .Select(t => (Target: t, Key: Math.Pow(random.NextDouble(), 1.0 / Math.Max(1, t.Weight))))
            .OrderByDescending(x => x.Key)
            .Select(x => x.Target)
            .ToList();
    }
}
