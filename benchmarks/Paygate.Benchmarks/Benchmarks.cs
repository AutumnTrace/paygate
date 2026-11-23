using System.Diagnostics.Metrics;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paygate.Application.Idempotency;
using Paygate.Application.Observability;
using Paygate.Application.Providers;
using Paygate.Application.Resilience;
using Paygate.Application.Routing;
using Paygate.Application.Webhooks;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;
using Polly;

namespace Paygate.Benchmarks;

/// <summary>Cost of turning the routing table into a candidate list for one payment (runs on every deposit).</summary>
[MemoryDiagnoser]
public class RoutingBenchmarks
{
    private RuleBasedRoutingEngine _engine = null!;
    private RoutingContext _nordicCard = null!;
    private RoutingContext _defaultCard = null!;
    private RoutingContext _crypto = null!;

    [GlobalSetup]
    public void Setup()
    {
        // The same table and providers as src/Paygate.Api/appsettings.json.
        var catalog = new ProviderCatalog(
        [
            Fixtures.Provider("heron", [PaymentMethod.Card], ["EUR", "GBP", "USD", "CAD", "SEK", "NOK", "DKK", "PLN"]),
            Fixtures.Provider("osprey", [PaymentMethod.Card, PaymentMethod.BankTransfer], ["EUR", "SEK", "NOK", "DKK", "PLN", "BRL"], ["AT", "BR", "DE", "DK", "FI", "NL", "NO", "PL", "SE"]),
            Fixtures.Provider("wren", [PaymentMethod.CryptoWallet], ["EUR", "USD", "CAD", "BRL"]),
        ]);

        var options = new RoutingOptions();
        options.Rules.Add(Fixtures.Rule("high-value-eur-cards", [("osprey", 1, 100), ("heron", 2, 100)], r => { r.Methods.Add(PaymentMethod.Card); r.Currencies.Add("EUR"); r.MinAmount = 500_000; }));
        options.Rules.Add(Fixtures.Rule("nordic-cards", [("heron", 1, 70), ("osprey", 1, 30)], r => { r.Methods.Add(PaymentMethod.Card); foreach (var c in new[] { "SE", "NO", "DK", "FI" }) { r.Countries.Add(c); } }));
        options.Rules.Add(Fixtures.Rule("cards", [("heron", 1, 100), ("osprey", 2, 100)], r => r.Methods.Add(PaymentMethod.Card)));
        options.Rules.Add(Fixtures.Rule("bank-transfers", [("osprey", 1, 100)], r => r.Methods.Add(PaymentMethod.BankTransfer)));
        options.Rules.Add(Fixtures.Rule("crypto", [("wren", 1, 100)], r => r.Methods.Add(PaymentMethod.CryptoWallet)));

        _engine = new RuleBasedRoutingEngine(new StaticOptionsMonitor<RoutingOptions>(options), catalog, new Random(42));
        _nordicCard = new RoutingContext(PaymentKind.Deposit, PaymentMethod.Card, Money.Of(2_500, "SEK"), "SE");
        _defaultCard = new RoutingContext(PaymentKind.Deposit, PaymentMethod.Card, Money.Of(2_500, "EUR"), "DE");
        _crypto = new RoutingContext(PaymentKind.Deposit, PaymentMethod.CryptoWallet, Money.Of(2_500, "EUR"), "DE");
    }

    [Benchmark(Description = "Plan: weighted split (nordic-cards)")]
    public RoutePlan WeightedSplit() => _engine.Plan(_nordicCard);

    [Benchmark(Description = "Plan: priority failover (cards)")]
    public RoutePlan PriorityFailover() => _engine.Plan(_defaultCard);

    [Benchmark(Description = "Plan: last rule (crypto)")]
    public RoutePlan LastRule() => _engine.Plan(_crypto);
}

/// <summary>Cost of the security checks on every inbound request.</summary>
[MemoryDiagnoser]
public class SecurityBenchmarks
{
    private const string Secret = "whsec_osprey_demo_8a3d6b1f0e9c2754";
    private readonly long _timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private byte[] _webhookBody = null!;
    private byte[] _requestBody = null!;
    private string _signature = null!;
    private string _timestampHeader = null!;
    private WebhookVerifier _oneSecret = null!;
    private WebhookVerifier _twoSecrets = null!;

    [GlobalSetup]
    public void Setup()
    {
        _webhookBody = Encoding.UTF8.GetBytes(
            """{"id":"evt_5f0e2c1d9a7b4e3f8c6d2a1b0e9f8d7c","type":"payment.captured","paymentId":"01a20863-a262-7f1c-8ebb-6b4dca9a79d4","providerReference":"osp_fe88309a48ec4e60","occurredAt":"2026-11-23T12:31:35.742747+00:00"}""");
        _requestBody = Encoding.UTF8.GetBytes(
            """{"amount":2500,"currency":"EUR","method":"card","country":"DE","customerId":"player-42","paymentToken":"tok_visa_4242"}""");
        _signature = WebhookSignature.Sign(Secret, _timestamp, _webhookBody);
        _timestampHeader = _timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _oneSecret = Verifier([Secret]);
        _twoSecrets = Verifier(["whsec_osprey_rotated_00000000000", Secret]);
    }

    [Benchmark(Description = "Webhook verify (1 secret)")]
    public WebhookVerificationResult VerifyOneSecret() => _oneSecret.Verify("osprey", _timestampHeader, _signature, _webhookBody);

    [Benchmark(Description = "Webhook verify (2 secrets, rotation)")]
    public WebhookVerificationResult VerifyTwoSecrets() => _twoSecrets.Verify("osprey", _timestampHeader, _signature, _webhookBody);

    [Benchmark(Description = "Idempotency fingerprint (SHA-256)")]
    public string Fingerprint() => RequestFingerprint.Compute("POST", "/v1/payments", _requestBody);

    private static WebhookVerifier Verifier(string[] secrets)
    {
        var options = new WebhookOptions();
        options.Secrets["osprey"] = secrets;
        return new WebhookVerifier(new StaticOptionsMonitor<WebhookOptions>(options), TimeProvider.System);
    }
}

/// <summary>What the per-provider resilience pipeline adds on top of a provider call that succeeds.</summary>
[MemoryDiagnoser]
public class ResiliencePipelineBenchmarks
{
    private static readonly ProviderResponse Captured = new(ProviderResponseStatus.Captured, "hrn_0123456789abcdef");
    private ResiliencePipeline _pipeline = null!;
    private ServiceProvider _services = null!;

    [GlobalSetup]
    public void Setup()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var health = new ProviderHealthRegistry();
        var metrics = new PaygateMetrics(_services.GetRequiredService<IMeterFactory>(), health);
        _pipeline = ProviderPipeline.Configure(new ResiliencePipelineBuilder(), new ProviderResilienceOptions(), health.For("heron"), metrics, NullLogger.Instance).Build();
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Baseline = true, Description = "Direct provider call")]
    public async Task<ProviderResponse> Direct() => await Task.FromResult(Captured);

    [Benchmark(Description = "Through pipeline (timeout, retry, breaker, timeout)")]
    public async Task<ProviderResponse> ThroughPipeline()
    {
        var context = ResilienceContextPool.Shared.Get();
        context.Properties.Set(ProviderPipeline.IsIdempotent, true);
        try
        {
            return await _pipeline.ExecuteAsync(static _ => ValueTask.FromResult(Captured), context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}

internal static class Fixtures
{
    public static IPaymentProvider Provider(string name, PaymentMethod[] methods, string[] currencies, string[]? countries = null) =>
        new FakeProvider(name, new ProviderCapabilities
        {
            Methods = methods.ToHashSet(),
            Currencies = currencies.ToHashSet(StringComparer.Ordinal),
            Countries = (countries ?? []).ToHashSet(StringComparer.Ordinal),
            SupportsPayouts = true,
            SupportsRefunds = true,
            SupportsIdempotencyKeys = true,
        });

    public static RoutingRule Rule(string name, (string Provider, int Priority, int Weight)[] targets, Action<RoutingRule> configure)
    {
        var rule = new RoutingRule { Name = name };
        foreach (var (provider, priority, weight) in targets)
        {
            rule.Targets.Add(new RouteTarget { Provider = provider, Priority = priority, Weight = weight });
        }

        configure(rule);
        return rule;
    }

    private sealed class FakeProvider(string name, ProviderCapabilities capabilities) : IPaymentProvider
    {
        public string Name => name;

        public string DisplayName => name;

        public ProviderCapabilities Capabilities => capabilities;

        public Task<ProviderResponse> ProcessAsync(ProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderRefundResponse> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
