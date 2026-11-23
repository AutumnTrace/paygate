using Microsoft.Extensions.Time.Testing;
using Paygate.Application.Routing;
using Paygate.Domain.Payments;
using Polly.CircuitBreaker;

namespace Paygate.UnitTests.Routing;

/// <summary>Failover behaviour of the router running on the real Polly pipelines.</summary>
public sealed class PaymentRouterTests
{
    [Fact]
    public async Task Routes_to_the_primary_provider_when_it_is_healthy()
    {
        using var harness = new RouterHarness();

        var (payment, result) = await harness.RouteAsync();

        Assert.Equal(RoutingOutcome.Accepted, result.Outcome);
        Assert.Equal(("alpha", PaymentStatus.Captured), (payment.Provider, payment.Status));
        Assert.Equal("cards", result.Rule);
        Assert.Equal(0, harness.Beta.Calls);
        Assert.Empty(harness.Failovers.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task Fails_over_to_the_secondary_when_the_primary_times_out()
    {
        using var harness = new RouterHarness();
        harness.Alpha.Behaviour = (_, ct) => ScriptedProvider.Hang(ct);

        var (payment, result) = await harness.RouteAsync();

        Assert.Equal(RoutingOutcome.Accepted, result.Outcome);
        Assert.Equal(("beta", PaymentStatus.Captured), (payment.Provider, payment.Status));
        Assert.Collection(
            payment.Attempts,
            a => Assert.Equal(("alpha", AttemptOutcome.TimedOut), (a.Provider, a.Outcome)),
            a => Assert.Equal(("beta", AttemptOutcome.Accepted), (a.Provider, a.Outcome)));

        // alpha supports idempotency keys, so the timeout was retried twice before failing over.
        Assert.Equal(3, harness.Alpha.Calls);
        var failover = Assert.Single(harness.Failovers.GetMeasurementSnapshot());
        Assert.Equal("timeout", failover.Tags["reason"]);
    }

    [Fact]
    public async Task Opens_the_circuit_after_repeated_failures_and_moves_traffic_to_the_next_provider()
    {
        using var harness = new RouterHarness();
        harness.Alpha.Behaviour = (_, _) => ScriptedProvider.Unavailable("alpha");

        var (first, _) = await harness.RouteAsync();
        var callsToOpen = harness.Alpha.Calls;
        var (second, secondResult) = await harness.RouteAsync();
        var (third, _) = await harness.RouteAsync();

        Assert.Equal("beta", first.Provider);
        Assert.Equal(CircuitState.Open, harness.Health.For("alpha").Circuit.CircuitState);

        // With the circuit open, alpha is not called at all; payments go straight to beta.
        Assert.Equal(callsToOpen, harness.Alpha.Calls);
        Assert.Equal(RoutingOutcome.Accepted, secondResult.Outcome);
        Assert.All([second, third], p =>
        {
            Assert.Equal("beta", p.Provider);
            Assert.Equal(AttemptOutcome.SkippedCircuitOpen, p.Attempts[0].Outcome);
        });

        var reasons = harness.Failovers.GetMeasurementSnapshot().Select(m => m.Tags["reason"]).ToList();
        Assert.Equal(["error", "circuit_open", "circuit_open"], reasons);
    }

    [Fact]
    public async Task Circuit_opening_during_retries_is_reported_as_what_happened_to_the_calls()
    {
        // With a threshold of 2, the breaker opens after the second timeout, so the third (retry)
        // attempt is rejected by the circuit. The attempt must still read "timed out": the provider
        // was called and may have received the request.
        using var harness = new RouterHarness(overrides: new() { ["Resilience:MinimumThroughput"] = "2" });
        harness.Alpha.Behaviour = (_, ct) => ScriptedProvider.Hang(ct);

        var (payment, _) = await harness.RouteAsync();

        Assert.Equal(2, harness.Alpha.Calls);
        var attempt = payment.Attempts[0];
        Assert.Equal(AttemptOutcome.TimedOut, attempt.Outcome);
        Assert.Equal("timed out (2 calls), then the circuit opened", attempt.Error);
        Assert.Equal("beta", payment.Provider);
    }

    [Fact]
    public async Task Half_open_circuit_lets_one_trial_call_through_and_closes_when_it_succeeds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var harness = new RouterHarness(time, alphaIdempotent: false);
        harness.Alpha.Behaviour = (_, _) => ScriptedProvider.Unavailable("alpha");

        for (var i = 0; i < 3; i++)
        {
            await harness.RouteAsync();
        }

        Assert.Equal(CircuitState.Open, harness.Health.For("alpha").Circuit.CircuitState);

        // After the break duration, the next payment becomes the trial call. alpha has recovered.
        harness.Alpha.Behaviour = (_, _) => ScriptedProvider.Capture();
        time.Advance(TimeSpan.FromSeconds(31));
        var (payment, _) = await harness.RouteAsync();

        Assert.Equal("alpha", payment.Provider);
        Assert.Equal(CircuitState.Closed, harness.Health.For("alpha").Circuit.CircuitState);
    }

    [Fact]
    public async Task Declines_are_final_and_never_fail_over()
    {
        using var harness = new RouterHarness();
        harness.Alpha.Behaviour = (_, _) => ScriptedProvider.Decline("insufficient_funds");

        var (payment, result) = await harness.RouteAsync();

        Assert.Equal(RoutingOutcome.Declined, result.Outcome);
        Assert.Equal((PaymentStatus.Failed, "insufficient_funds"), (payment.Status, payment.FailureCode));
        Assert.Equal((1, 0), (harness.Alpha.Calls, harness.Beta.Calls));
        Assert.Equal(CircuitState.Closed, harness.Health.For("alpha").Circuit.CircuitState);
    }

    [Fact]
    public async Task Retries_an_idempotent_provider_with_the_same_idempotency_key()
    {
        using var harness = new RouterHarness();
        harness.Alpha.Behaviour = (call, _) => call == 1 ? ScriptedProvider.Unavailable("alpha") : ScriptedProvider.Capture();

        var (payment, _) = await harness.RouteAsync();

        Assert.Equal("alpha", payment.Provider);
        Assert.Equal(2, harness.Alpha.Calls);
        Assert.Single(harness.Alpha.IdempotencyKeys.Distinct());
        Assert.Equal(PaymentRouter.IdempotencyKeyFor(payment), harness.Alpha.IdempotencyKeys[0]);
    }

    [Fact]
    public async Task Never_retries_a_provider_that_cannot_deduplicate()
    {
        using var harness = new RouterHarness(alphaIdempotent: false);
        harness.Alpha.Behaviour = (call, _) => call == 1 ? ScriptedProvider.Unavailable("alpha") : ScriptedProvider.Capture();

        var (payment, _) = await harness.RouteAsync();

        Assert.Equal(1, harness.Alpha.Calls);
        Assert.Equal("beta", payment.Provider);
    }

    [Fact]
    public async Task A_timed_out_payout_waits_for_confirmation_instead_of_failing_over()
    {
        using var harness = new RouterHarness();
        harness.Alpha.Behaviour = (_, ct) => ScriptedProvider.Hang(ct);

        var (payout, result) = await harness.RouteAsync(PaymentKind.Payout);

        Assert.Equal(RoutingOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal((PaymentStatus.Pending, "alpha"), (payout.Status, payout.Provider));
        Assert.Null(payout.ProviderReference);
        Assert.Equal(0, harness.Beta.Calls);
    }

    [Fact]
    public async Task Fails_with_no_provider_available_when_every_candidate_fails()
    {
        using var harness = new RouterHarness();
        harness.Alpha.Behaviour = (_, _) => ScriptedProvider.Unavailable("alpha");
        harness.Beta.Behaviour = (_, ct) => ScriptedProvider.Hang(ct);

        var (payment, result) = await harness.RouteAsync();

        Assert.Equal(RoutingOutcome.AllProvidersFailed, result.Outcome);
        Assert.Equal((PaymentStatus.Failed, "no_provider_available"), (payment.Status, payment.FailureCode));
        Assert.Contains("alpha", payment.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("beta", payment.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_with_no_route_when_no_provider_supports_the_currency()
    {
        using var harness = new RouterHarness();

        var (payment, result) = await harness.RouteAsync(currency: "USD");

        Assert.Equal(RoutingOutcome.NoRoute, result.Outcome);
        Assert.Equal("no_route", payment.FailureCode);
        Assert.Equal((0, 0), (harness.Alpha.Calls, harness.Beta.Calls));
    }
}
