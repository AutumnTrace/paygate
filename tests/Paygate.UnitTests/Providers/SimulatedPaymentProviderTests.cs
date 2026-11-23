using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Paygate.Application.Providers;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;
using Paygate.Infrastructure;
using Paygate.Infrastructure.Providers;

namespace Paygate.UnitTests.Providers;

public sealed class SimulatedPaymentProviderTests : IDisposable
{
    private readonly ServiceProvider _services;

    public SimulatedPaymentProviderTests()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:heron:DisplayName"] = "Heron Acquiring",
            ["Providers:heron:ReferencePrefix"] = "hrn",
            ["Providers:heron:Methods:0"] = "Card",
            ["Providers:heron:Currencies:0"] = "EUR",
            ["Providers:heron:SupportsRefunds"] = "true",
            ["Providers:heron:Faults:LatencyMs"] = "0",
            ["Providers:osprey:ReferencePrefix"] = "osp",
            ["Providers:osprey:Methods:0"] = "BankTransfer",
            ["Providers:osprey:Currencies:0"] = "SEK",
            ["Providers:osprey:Countries:0"] = "SE",
            ["Providers:osprey:AsyncMethods:0"] = "BankTransfer",
            ["Providers:osprey:SupportsPayouts"] = "true",
            ["Providers:osprey:Faults:LatencyMs"] = "0",
        }).Build();

        _services = new ServiceCollection().AddSimulatedProviders(configuration).BuildServiceProvider();
    }

    private IPaymentProvider Provider(string name) => _services.GetServices<IPaymentProvider>().Single(p => p.Name == name);

    private ProviderFaultRegistry Faults => _services.GetRequiredService<ProviderFaultRegistry>();

    private static ProviderRequest Request(string key, long amount = 1_000, PaymentMethod method = PaymentMethod.Card, string currency = "EUR") =>
        new(Guid.NewGuid(), key, PaymentKind.Deposit, method, Money.Of(amount, currency), "SE", "player-1", "tok_1");

    [Fact]
    public void Binds_capabilities_from_configuration()
    {
        var heron = Provider("heron");
        var osprey = Provider("osprey");

        Assert.Equal("Heron Acquiring", heron.DisplayName);
        Assert.True(heron.Capabilities.Supports(PaymentKind.Deposit, PaymentMethod.Card, "EUR", "DE"));
        Assert.False(heron.Capabilities.Supports(PaymentKind.Payout, PaymentMethod.Card, "EUR", "DE"));
        Assert.True(osprey.Capabilities.Supports(PaymentKind.Payout, PaymentMethod.BankTransfer, "SEK", "SE"));
        Assert.False(osprey.Capabilities.Supports(PaymentKind.Deposit, PaymentMethod.BankTransfer, "SEK", "DE"));
    }

    [Fact]
    public async Task Deduplicates_by_idempotency_key_like_a_real_psp()
    {
        var heron = Provider("heron");

        var first = await heron.ProcessAsync(Request("key-1"), TestContext.Current.CancellationToken);
        var retry = await heron.ProcessAsync(Request("key-1"), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderResponseStatus.Captured, first.Status);
        Assert.Equal(first.ProviderReference, retry.ProviderReference);
        Assert.StartsWith("hrn_", first.ProviderReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declines_amounts_ending_in_51()
    {
        var response = await Provider("heron").ProcessAsync(Request("key-2", amount: 2_551), TestContext.Current.CancellationToken);

        Assert.Equal((ProviderResponseStatus.Declined, "insufficient_funds"), (response.Status, response.DeclineCode));
    }

    [Fact]
    public async Task Async_methods_answer_pending()
    {
        var response = await Provider("osprey").ProcessAsync(
            Request("key-3", method: PaymentMethod.BankTransfer, currency: "SEK"), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderResponseStatus.Pending, response.Status);
    }

    [Fact]
    public async Task Error_outage_fails_fast_with_a_transient_error()
    {
        Faults.Set("heron", new ProviderFaults { Outage = OutageMode.Errors, LatencyMs = 0 });

        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => Provider("heron").ProcessAsync(Request("key-4"), TestContext.Current.CancellationToken));

        Assert.Equal("heron", ex.Provider);
    }

    [Fact]
    public async Task Timeout_outage_hangs_until_the_caller_gives_up()
    {
        Faults.Set("heron", new ProviderFaults { Outage = OutageMode.Timeouts, LatencyMs = 0 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider("heron").ProcessAsync(Request("key-5"), cts.Token));
    }

    [Fact]
    public void Rejects_out_of_range_fault_settings() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Faults.Set("heron", new ProviderFaults { ErrorRate = 1.5 }));

    public void Dispose() => _services.Dispose();
}
