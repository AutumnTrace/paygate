using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Paygate.Application;
using Paygate.Application.Providers;
using Paygate.Application.Resilience;
using Paygate.Application.Routing;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.UnitTests.Routing;

/// <summary>
/// Wires the real routing stack (engine, invoker, Polly pipelines, metrics) through DI,
/// with two scripted providers: <c>alpha</c> (priority 1) and <c>beta</c> (priority 2).
/// </summary>
internal sealed class RouterHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public RouterHarness(TimeProvider? time = null, bool alphaIdempotent = true, Dictionary<string, string?>? overrides = null)
    {
        Alpha = new ScriptedProvider("alpha", alphaIdempotent);
        Beta = new ScriptedProvider("beta");

        var settings = new Dictionary<string, string?>
        {
            ["Routing:Rules:0:Name"] = "cards",
            ["Routing:Rules:0:Methods:0"] = "Card",
            ["Routing:Rules:0:Targets:0:Provider"] = "alpha",
            ["Routing:Rules:0:Targets:0:Priority"] = "1",
            ["Routing:Rules:0:Targets:1:Provider"] = "beta",
            ["Routing:Rules:0:Targets:1:Priority"] = "2",
            ["Resilience:AttemptTimeout"] = "00:00:00.150",
            ["Resilience:TotalTimeout"] = "00:00:02",
            ["Resilience:RetryBaseDelay"] = "00:00:00.001",
            ["Resilience:MaxRetryAttempts"] = "2",
            ["Resilience:MinimumThroughput"] = "3",
            ["Resilience:FailureRatio"] = "0.5",
            ["Resilience:BreakDuration"] = "00:00:30",
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(time ?? TimeProvider.System)
            .AddSingleton<IPaymentProvider>(Alpha)
            .AddSingleton<IPaymentProvider>(Beta)
            .AddPaygateRouting(configuration);

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _ = _services.GetRequiredService<IOptions<RoutingOptions>>().Value; // runs the validators like ValidateOnStart would

        Router = _services.GetRequiredService<PaymentRouter>();
        Health = _services.GetRequiredService<ProviderHealthRegistry>();
        Failovers = new MetricCollector<long>(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), "Paygate", "paygate.routing.failovers");
    }

    public ScriptedProvider Alpha { get; }

    public ScriptedProvider Beta { get; }

    public PaymentRouter Router { get; }

    public ProviderHealthRegistry Health { get; }

    public MetricCollector<long> Failovers { get; }

    public static Payment NewPayment(PaymentKind kind = PaymentKind.Deposit, string currency = "EUR", long amount = 2_500) =>
        Payment.Create(Guid.CreateVersion7(), "m_test", kind, PaymentMethod.Card, Money.Of(amount, currency), "SE", "player-1", DateTimeOffset.UtcNow);

    public async Task<(Payment Payment, RoutingResult Result)> RouteAsync(PaymentKind kind = PaymentKind.Deposit, string currency = "EUR")
    {
        var payment = NewPayment(kind, currency);
        var result = await Router.RouteAsync(payment, "tok_test", TestContext.Current.CancellationToken);
        return (payment, result);
    }

    public void Dispose()
    {
        Failovers.Dispose();
        _services.Dispose();
    }
}
