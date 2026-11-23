using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Paygate.Api.Tests;

/// <summary>
/// The live failover demo from the README, as tests: break a provider through the admin API and
/// watch circuits open and traffic move. Each test gets its own app, so circuit state never leaks.
/// </summary>
public sealed class FailoverApiTests : IDisposable
{
    private readonly PaygateApiFactory _factory = new();
    private readonly HttpClient _merchant;
    private readonly HttpClient _admin;

    public FailoverApiTests()
    {
        _merchant = _factory.CreateClient(PaygateApiFactory.MerchantKey);
        _admin = _factory.CreateClient(PaygateApiFactory.AdminKey);
    }

    private async Task SetFaultsAsync(string provider, object faults)
    {
        var response = await _admin.PutAsJsonAsync($"/v1/admin/providers/{provider}/faults", faults, TestContext.Current.CancellationToken);
        await response.ReadSuccessJsonAsync(HttpStatusCode.OK);
    }

    private async Task<JsonNode> ProviderAsync(string name)
    {
        var providers = await _merchant.GetFromJsonAsync<JsonArray>("/v1/providers", TestContext.Current.CancellationToken);
        return providers!.Single(p => p!.Str("name") == name)!;
    }

    [Fact]
    public async Task Timeouts_at_the_primary_fail_over_and_open_its_circuit()
    {
        await SetFaultsAsync("heron", new { outage = "timeouts" });

        var first = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit())).ReadSuccessJsonAsync(HttpStatusCode.Created);
        var second = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit())).ReadSuccessJsonAsync(HttpStatusCode.Created);

        Assert.Equal(("captured", "osprey"), (first.Str("status"), first.Str("provider")));
        Assert.Equal("timed_out", first.Str("attempts.0.outcome"));
        Assert.Equal("accepted", first.Str("attempts.1.outcome"));

        // The breaker opened during the first payment, so the second one skips heron without waiting.
        Assert.Equal("skipped_circuit_open", second.Str("attempts.0.outcome"));
        Assert.Equal("0", second.Str("attempts.0.durationMs"));
        Assert.Equal("osprey", second.Str("provider"));

        var heron = await ProviderAsync("heron");
        Assert.Equal("open", heron.Str("circuit.state"));
        Assert.Equal("timeouts", heron.Str("faults.outage"));
    }

    [Fact]
    public async Task Isolating_a_provider_takes_it_out_of_rotation_until_closed()
    {
        var isolate = await _admin.PostAsync("/v1/admin/providers/heron/circuit/isolate", null, TestContext.Current.CancellationToken);
        var whileIsolated = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit())).ReadJsonAsync();
        await _admin.PostAsync("/v1/admin/providers/heron/circuit/close", null, TestContext.Current.CancellationToken);
        var afterClose = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit())).ReadJsonAsync();

        Assert.Equal("isolated", (await isolate.ReadJsonAsync()).Str("circuit.state"));
        Assert.Equal("osprey", whileIsolated.Str("provider"));
        Assert.Equal("heron", afterClose.Str("provider"));
    }

    [Fact]
    public async Task When_every_eligible_provider_is_down_the_payment_fails_cleanly()
    {
        await SetFaultsAsync("heron", new { outage = "errors" });
        await SetFaultsAsync("osprey", new { outage = "errors" });

        var payment = await (await _merchant.PostIdempotentAsync("/v1/payments", HttpTestExtensions.CardDeposit())).ReadSuccessJsonAsync(HttpStatusCode.Created);

        Assert.Equal(("failed", "no_provider_available"), (payment.Str("status"), payment.Str("failure.code")));
    }

    [Fact]
    public async Task A_payout_that_times_out_stays_pending_and_is_never_sent_twice()
    {
        await SetFaultsAsync("wren", new { outage = "timeouts" });

        var payout = await (await _merchant.PostIdempotentAsync("/v1/payouts", new
        {
            amount = 20_000,
            currency = "EUR",
            method = "crypto_wallet",
            country = "DE",
            customerId = "player-3",
            destinationToken = "tok_wallet_btc_02",
        })).ReadSuccessJsonAsync(HttpStatusCode.Created);

        Assert.Equal(("pending", "wren"), (payout.Str("status"), payout.Str("provider")));
        var attempt = Assert.Single(payout["attempts"]!.AsArray());
        Assert.Equal("timed_out", attempt!.Str("outcome"));
    }

    [Fact]
    public async Task Merchants_cannot_inject_faults()
    {
        var response = await _merchant.PutAsJsonAsync("/v1/admin/providers/heron/faults", new { outage = "errors" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Fault_settings_are_validated()
    {
        var response = await _admin.PutAsJsonAsync("/v1/admin/providers/heron/faults", new { errorRate = 2.0 }, TestContext.Current.CancellationToken);
        var unknown = await _admin.PutAsJsonAsync("/v1/admin/providers/nope/faults", new { errorRate = 0.1 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
