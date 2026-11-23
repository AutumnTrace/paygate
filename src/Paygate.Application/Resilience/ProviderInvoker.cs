using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Paygate.Application.Observability;
using Paygate.Application.Providers;
using Paygate.Domain.Payments;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Timeout;

namespace Paygate.Application.Resilience;

/// <summary>The outcome of one provider call made through its resilience pipeline.</summary>
public sealed record ProviderCallResult<T>(AttemptOutcome Outcome, TimeSpan Duration, T? Response, string? Error)
    where T : class;

/// <summary>
/// Calls a provider through its named resilience pipeline and turns every way the call can end
/// (answer, decline, timeout, error, open circuit) into a value, so callers never catch Polly exceptions.
/// </summary>
public sealed partial class ProviderInvoker(
    ResiliencePipelineProvider<string> pipelines,
    ProviderHealthRegistry health,
    PaygateMetrics metrics,
    TimeProvider time,
    ILogger<ProviderInvoker> logger)
{
    public Task<ProviderCallResult<ProviderResponse>> ProcessAsync(
        IPaymentProvider provider, ProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        return InvokeAsync(
            provider,
            request.Kind == PaymentKind.Deposit ? "deposit" : "payout",
            static (p, r, ct) => p.ProcessAsync(r, ct),
            request,
            static response => response.Status == ProviderResponseStatus.Declined ? AttemptOutcome.Declined : AttemptOutcome.Accepted,
            cancellationToken);
    }

    public Task<ProviderCallResult<ProviderRefundResponse>> RefundAsync(
        IPaymentProvider provider, ProviderRefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        return InvokeAsync(
            provider,
            "refund",
            static (p, r, ct) => p.RefundAsync(r, ct),
            request,
            static _ => AttemptOutcome.Accepted,
            cancellationToken);
    }

    private async Task<ProviderCallResult<TResponse>> InvokeAsync<TRequest, TResponse>(
        IPaymentProvider provider,
        string operation,
        Func<IPaymentProvider, TRequest, CancellationToken, Task<TResponse>> call,
        TRequest request,
        Func<TResponse, AttemptOutcome> classify,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var activity = PaygateTelemetry.ActivitySource.StartActivity($"psp {operation}", ActivityKind.Client);
        activity?.SetTag("paygate.provider", provider.Name);
        activity?.SetTag("paygate.operation", operation);

        var providerHealth = health.For(provider.Name);
        var pipeline = pipelines.GetPipeline(provider.Name);
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(ProviderPipeline.IsIdempotent, provider.Capabilities.SupportsIdempotencyKeys);
        var tracker = new CallTracker();
        var started = time.GetTimestamp();

        try
        {
            var response = await pipeline.ExecuteAsync(
                static async (ctx, state) =>
                {
                    state.tracker.Calls++;
                    try
                    {
                        return await state.call(state.provider, state.request, ctx.CancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!state.outer.IsCancellationRequested)
                    {
                        state.tracker.LastFailureWasTimeout = true; // Cancelled by an attempt or total timeout.
                        throw;
                    }
                    catch (ProviderUnavailableException ex)
                    {
                        state.tracker.LastFailureWasTimeout = false;
                        state.tracker.LastError = ex.Message;
                        throw;
                    }
                },
                context,
                (provider, request, call, tracker, outer: cancellationToken)).ConfigureAwait(false);

            var elapsed = time.GetElapsedTime(started);
            var outcome = classify(response);
            providerHealth.RecordSuccess(elapsed); // A decline is a healthy answer from a working provider.
            metrics.ProviderCall(provider.Name, operation, outcome == AttemptOutcome.Declined ? "declined" : "success", elapsed);
            activity?.SetTag("paygate.outcome", outcome.ToString());
            return new ProviderCallResult<TResponse>(outcome, elapsed, response, null);
        }
        catch (BrokenCircuitException) when (tracker.Calls == 0)
        {
            // Rejected by the open circuit before reaching the provider: nothing was sent.
            var elapsed = time.GetElapsedTime(started);
            metrics.ProviderCall(provider.Name, operation, "circuit_open", elapsed);
            activity?.SetStatus(ActivityStatusCode.Error, "circuit open");
            return new ProviderCallResult<TResponse>(AttemptOutcome.SkippedCircuitOpen, elapsed, null, "circuit open");
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or ProviderUnavailableException or BrokenCircuitException
            && !cancellationToken.IsCancellationRequested)
        {
            // The provider was called (possibly several times) and never gave a usable answer.
            // If the circuit opened during our own retries, report what actually happened to the calls.
            var elapsed = time.GetElapsedTime(started);
            var timedOut = ex is TimeoutRejectedException || (ex is BrokenCircuitException && tracker.LastFailureWasTimeout);
            var error = Describe(tracker, timedOut, ex is BrokenCircuitException);

            providerHealth.RecordFailure(elapsed, error, time.GetUtcNow());
            metrics.ProviderCall(provider.Name, operation, timedOut ? "timeout" : "error", elapsed);
            activity?.SetStatus(ActivityStatusCode.Error, error);
            LogCallFailed(logger, ex, provider.Name, operation, error);
            return new ProviderCallResult<TResponse>(timedOut ? AttemptOutcome.TimedOut : AttemptOutcome.Failed, elapsed, null, error);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private static string Describe(CallTracker tracker, bool timedOut, bool circuitOpened)
    {
        var what = timedOut ? "timed out" : tracker.LastError ?? "failed";
        var calls = tracker.Calls == 1 ? "1 call" : $"{tracker.Calls} calls";
        return circuitOpened ? $"{what} ({calls}), then the circuit opened" : $"{what} ({calls})";
    }

    /// <summary>What happened inside the pipeline, across retries.</summary>
    private sealed class CallTracker
    {
        public int Calls { get; set; }

        public bool LastFailureWasTimeout { get; set; }

        public string? LastError { get; set; }
    }

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "Call to {Provider} ({Operation}) failed: {Error}")]
    private static partial void LogCallFailed(ILogger logger, Exception exception, string provider, string operation, string error);
}
