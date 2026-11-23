using Microsoft.Extensions.Logging;
using Paygate.Application.Observability;
using Paygate.Application.Providers;
using Paygate.Application.Resilience;
using Paygate.Domain.Payments;

namespace Paygate.Application.Routing;

public enum RoutingOutcome
{
    /// <summary>A provider accepted the payment (captured, authorized or pending).</summary>
    Accepted,

    /// <summary>A provider declined it. Final: declines are never failed over.</summary>
    Declined,

    /// <summary>A payout call timed out; it waits in Pending for the provider's confirmation.</summary>
    OutcomeUnknown,

    /// <summary>No routing rule or capable provider exists for this payment.</summary>
    NoRoute,

    /// <summary>Every candidate failed, timed out or had an open circuit.</summary>
    AllProvidersFailed,
}

public sealed record RoutingResult(RoutingOutcome Outcome, string? Rule, string? Provider);

/// <summary>
/// Sends a payment to the first healthy provider in its route plan, failing over down the list
/// on technical failures. The payment aggregate records every attempt and the final status.
/// </summary>
public sealed partial class PaymentRouter(
    IRoutingEngine engine,
    ProviderCatalog catalog,
    ProviderInvoker invoker,
    PaygateMetrics metrics,
    TimeProvider time,
    ILogger<PaymentRouter> logger)
{
    public async Task<RoutingResult> RouteAsync(Payment payment, string? instrumentToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);

        using var activity = PaygateTelemetry.ActivitySource.StartActivity("paygate.route");
        activity?.SetTag("paygate.payment_id", payment.Id.ToString());
        activity?.SetTag("paygate.kind", payment.Kind.ToString());
        activity?.SetTag("paygate.method", payment.Method.ToString());
        activity?.SetTag("paygate.currency", payment.Amount.Currency.Code);

        var kind = payment.Kind.ToString();
        var plan = engine.Plan(new RoutingContext(payment.Kind, payment.Method, payment.Amount, payment.Country));
        activity?.SetTag("paygate.routing.rule", plan.Rule);
        activity?.SetTag("paygate.routing.candidates", string.Join(",", plan.Candidates.Select(c => c.Provider)));

        if (plan.IsEmpty)
        {
            payment.MarkFailed(
                "no_route",
                $"No provider can process a {payment.Method} {payment.Kind.ToString().ToLowerInvariant()} in {payment.Amount.Currency.Code} for {payment.Country}.",
                time.GetUtcNow());
            return Finish(payment, RoutingOutcome.NoRoute, plan.Rule);
        }

        var failures = new List<string>(plan.Candidates.Count);
        foreach (var candidate in plan.Candidates)
        {
            var provider = catalog.Get(candidate.Provider);

            // No separate health check: when the circuit is open, Polly rejects the call before it
            // reaches the provider (it costs microseconds), and once the break is over the same call
            // becomes the half-open trial. Reading the state alone would never let that trial through.
            var request = new ProviderRequest(
                payment.Id,
                IdempotencyKeyFor(payment),
                payment.Kind,
                payment.Method,
                payment.Amount,
                payment.Country,
                payment.CustomerId,
                instrumentToken);

            var result = await invoker.ProcessAsync(provider, request, cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            payment.RecordAttempt(provider.Name, result.Outcome, result.Duration, result.Error ?? result.Response?.DeclineCode, now);

            switch (result.Outcome)
            {
                case AttemptOutcome.Accepted:
                    payment.MarkAccepted(provider.Name, result.Response!.ProviderReference!, ToStatus(result.Response.Status), now);
                    return Finish(payment, RoutingOutcome.Accepted, plan.Rule);

                case AttemptOutcome.Declined:
                    payment.MarkFailed(
                        result.Response!.DeclineCode ?? "declined",
                        $"Declined by {provider.Name}. Declines are final and are not retried on another provider.",
                        now);
                    return Finish(payment, RoutingOutcome.Declined, plan.Rule);

                case AttemptOutcome.TimedOut when payment.Kind == PaymentKind.Payout:
                    // The money may already be on its way. Sending it again through another provider
                    // could pay the player twice, so wait for this provider's confirmation instead.
                    payment.MarkOutcomeUnknown(provider.Name, now);
                    LogPayoutOutcomeUnknown(logger, payment.Id, provider.Name);
                    return Finish(payment, RoutingOutcome.OutcomeUnknown, plan.Rule);

                default:
                    var reason = result.Outcome switch
                    {
                        AttemptOutcome.TimedOut => "timeout",
                        AttemptOutcome.SkippedCircuitOpen => "circuit_open",
                        _ => "error",
                    };
                    failures.Add($"{provider.Name} ({result.Error})");
                    metrics.Failover(provider.Name, reason, kind);
                    if (result.Outcome == AttemptOutcome.SkippedCircuitOpen)
                    {
                        LogSkipped(logger, payment.Id, provider.Name);
                    }
                    else
                    {
                        LogFailover(logger, payment.Id, provider.Name, reason);
                    }

                    break;
            }
        }

        payment.MarkFailed(
            "no_provider_available",
            $"Every eligible provider failed: {string.Join(", ", failures)}.",
            time.GetUtcNow());
        return Finish(payment, RoutingOutcome.AllProvidersFailed, plan.Rule);
    }

    /// <summary>
    /// The key the provider uses to deduplicate. Stable per payment, so a retry after a timeout
    /// can never create a second charge at the same provider.
    /// </summary>
    public static string IdempotencyKeyFor(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return $"pg_{payment.Id:N}";
    }

    private RoutingResult Finish(Payment payment, RoutingOutcome outcome, string? rule)
    {
        metrics.PaymentRouted(payment.Kind.ToString(), payment.Method.ToString(), payment.Provider, payment.Status.ToString());
        LogRouted(logger, payment.Id, outcome, payment.Provider ?? "none", payment.Status, payment.Attempts.Count);
        return new RoutingResult(outcome, rule, payment.Provider);
    }

    private static PaymentStatus ToStatus(ProviderResponseStatus status) => status switch
    {
        ProviderResponseStatus.Captured => PaymentStatus.Captured,
        ProviderResponseStatus.Authorized => PaymentStatus.Authorized,
        ProviderResponseStatus.Pending => PaymentStatus.Pending,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "Payment {PaymentId} routed: {Outcome} via {Provider}, status {Status} after {Attempts} attempt(s)")]
    private static partial void LogRouted(ILogger logger, Guid paymentId, RoutingOutcome outcome, string provider, PaymentStatus status, int attempts);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "Payment {PaymentId}: failing over from {Provider} ({Reason})")]
    private static partial void LogFailover(ILogger logger, Guid paymentId, string provider, string reason);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "Payment {PaymentId}: skipping {Provider}, its circuit is open")]
    private static partial void LogSkipped(ILogger logger, Guid paymentId, string provider);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Payout {PaymentId} timed out at {Provider}; left Pending until the provider confirms (no failover for payouts)")]
    private static partial void LogPayoutOutcomeUnknown(ILogger logger, Guid paymentId, string provider);
}
