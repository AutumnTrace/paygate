using Microsoft.Extensions.Options;
using Paygate.Application.Providers;
using Paygate.Application.Routing;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.UnitTests.Routing;

public sealed class RuleBasedRoutingEngineTests
{
    private static readonly ProviderCatalog Catalog = new(
    [
        new FakeProvider("card-a", [PaymentMethod.Card], ["EUR", "SEK", "GBP"]),
        new FakeProvider("card-b", [PaymentMethod.Card], ["EUR", "SEK"], countries: ["SE", "NO"]),
        new FakeProvider("bank", [PaymentMethod.BankTransfer], ["EUR", "SEK"], payouts: true),
        new FakeProvider("crypto", [PaymentMethod.CryptoWallet], ["EUR", "USD"], payouts: true),
    ]);

    private static RuleBasedRoutingEngine Engine(RoutingOptions options, int seed = 7) =>
        new(new StaticOptionsMonitor<RoutingOptions>(options), Catalog, new Random(seed));

    private static RoutingContext Card(long amount = 5_000, string currency = "EUR", string country = "SE", PaymentKind kind = PaymentKind.Deposit) =>
        new(kind, PaymentMethod.Card, Money.Of(amount, currency), country);

    private static RoutingRule Rule(string name, IEnumerable<(string Provider, int Priority, int Weight)> targets, Action<RoutingRule>? configure = null)
    {
        var rule = new RoutingRule { Name = name };
        foreach (var (provider, priority, weight) in targets)
        {
            rule.Targets.Add(new RouteTarget { Provider = provider, Priority = priority, Weight = weight });
        }

        configure?.Invoke(rule);
        return rule;
    }

    private static RoutingOptions Table(params RoutingRule[] rules)
    {
        var options = new RoutingOptions();
        foreach (var rule in rules)
        {
            options.Rules.Add(rule);
        }

        return options;
    }

    [Fact]
    public void Orders_candidates_by_priority()
    {
        var engine = Engine(Table(Rule("cards", [("card-b", 2, 100), ("card-a", 1, 100)], r => r.Methods.Add(PaymentMethod.Card))));

        var plan = engine.Plan(Card());

        Assert.Equal("cards", plan.Rule);
        Assert.Equal(["card-a", "card-b"], plan.Candidates.Select(c => c.Provider));
    }

    [Fact]
    public void First_matching_rule_wins()
    {
        var engine = Engine(Table(
            Rule("nordic-cards", [("card-b", 1, 100), ("card-a", 2, 100)], r => { r.Methods.Add(PaymentMethod.Card); r.Countries.Add("SE"); }),
            Rule("cards", [("card-a", 1, 100)], r => r.Methods.Add(PaymentMethod.Card))));

        Assert.Equal("nordic-cards", engine.Plan(Card(country: "SE")).Rule);
        Assert.Equal("cards", engine.Plan(Card(country: "DE")).Rule);
    }

    [Theory]
    [InlineData(99_999, "standard")]
    [InlineData(100_000, "high-value")]
    [InlineData(5_000_000, "high-value")]
    public void Amount_bands_are_inclusive_below_and_exclusive_above(long amount, string expectedRule)
    {
        var engine = Engine(Table(
            Rule("high-value", [("card-b", 1, 100)], r => { r.Currencies.Add("EUR"); r.MinAmount = 100_000; }),
            Rule("standard", [("card-a", 1, 100)], r => { r.Currencies.Add("EUR"); r.MaxAmount = 100_000; })));

        Assert.Equal(expectedRule, engine.Plan(Card(amount)).Rule);
    }

    [Fact]
    public void Skips_providers_that_cannot_process_the_payment()
    {
        // card-b does not support GBP, and only serves SE and NO.
        var engine = Engine(Table(Rule("cards", [("card-b", 1, 100), ("card-a", 2, 100)])));

        Assert.Equal(["card-a"], engine.Plan(Card(currency: "GBP")).Candidates.Select(c => c.Provider));
        Assert.Equal(["card-a"], engine.Plan(Card(country: "DE")).Candidates.Select(c => c.Provider));
    }

    [Fact]
    public void Payouts_only_go_to_providers_that_support_them()
    {
        var engine = Engine(Table(Rule("all", [("card-a", 1, 100), ("crypto", 2, 100)])));

        var plan = engine.Plan(new RoutingContext(PaymentKind.Payout, PaymentMethod.CryptoWallet, Money.Of(100, "EUR"), "SE"));

        Assert.Equal(["crypto"], plan.Candidates.Select(c => c.Provider));
    }

    [Fact]
    public void Returns_an_empty_plan_when_no_rule_matches()
    {
        var engine = Engine(Table(Rule("crypto", [("crypto", 1, 100)], r => r.Methods.Add(PaymentMethod.CryptoWallet))));

        var plan = engine.Plan(Card());

        Assert.True(plan.IsEmpty);
        Assert.Null(plan.Rule);
    }

    [Fact]
    public void Equal_priorities_split_traffic_by_weight_and_keep_the_other_as_failover()
    {
        var engine = Engine(Table(Rule("split", [("card-a", 1, 80), ("card-b", 1, 20)])), seed: 42);
        const int Draws = 10_000;

        var plans = Enumerable.Range(0, Draws).Select(_ => engine.Plan(Card())).ToList();
        var shareA = plans.Count(p => p.Candidates[0].Provider == "card-a") / (double)Draws;

        Assert.InRange(shareA, 0.77, 0.83);
        Assert.All(plans, p => Assert.Equal(2, p.Candidates.Count));
    }

    [Fact]
    public void Validator_rejects_unknown_providers_and_inverted_amount_bands()
    {
        var options = Table(
            Rule("typo", [("card-x", 1, 100)]),
            Rule("bands", [("card-a", 1, 100)], r => { r.MinAmount = 500; r.MaxAmount = 100; }),
            Rule("weights", [("card-a", 0, 100)]));

        var result = new RoutingOptionsValidator(Catalog).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("unknown provider 'card-x'", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("MinAmount must be lower", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("must be at least 1", StringComparison.Ordinal));
    }

    private sealed class FakeProvider(string name, PaymentMethod[] methods, string[] currencies, string[]? countries = null, bool payouts = false)
        : IPaymentProvider
    {
        public string Name => name;

        public string DisplayName => name;

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Methods = methods.ToHashSet(),
            Currencies = currencies.ToHashSet(),
            Countries = (countries ?? []).ToHashSet(),
            SupportsPayouts = payouts,
        };

        public Task<ProviderResponse> ProcessAsync(ProviderRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderRefundResponse> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
