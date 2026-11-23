using Microsoft.Extensions.Logging;
using Paygate.Application.Observability;
using Paygate.Application.Providers;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Paygate.Application.Resilience;

/// <summary>
/// Builds the resilience pipeline that wraps every call to one provider:
/// <code>
/// total timeout -> retry (idempotent calls only, exponential + jitter) -> circuit breaker -> attempt timeout -> PSP
/// </code>
/// The circuit breaker sits inside the retry, so every attempt counts towards opening it,
/// and an open circuit short-circuits the retries too.
/// </summary>
public static partial class ProviderPipeline
{
    /// <summary>Set per call. Retries are only allowed when the provider deduplicates by idempotency key.</summary>
    public static readonly ResiliencePropertyKey<bool> IsIdempotent = new("paygate.is-idempotent");

    public static ResiliencePipelineBuilder Configure(
        ResiliencePipelineBuilder builder,
        ProviderResilienceOptions options,
        ProviderHealth health,
        PaygateMetrics metrics,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        var provider = health.Provider;
        var timeProvider = builder.TimeProvider ?? TimeProvider.System;

        return builder
            .AddTimeout(new TimeoutStrategyOptions { Name = "total-timeout", Timeout = options.TotalTimeout })
            .AddRetry(new RetryStrategyOptions
            {
                Name = "retry",
                MaxRetryAttempts = Math.Max(1, options.MaxRetryAttempts),
                Delay = options.RetryBaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args => ValueTask.FromResult(
                    options.MaxRetryAttempts > 0
                    && IsTransient(args.Outcome.Exception)
                    && args.Context.Properties.GetValue(IsIdempotent, false)),
                OnRetry = args =>
                {
                    LogRetry(logger, provider, args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds, args.Outcome.Exception?.GetType().Name);
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                Name = "circuit-breaker",
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.MinimumThroughput,
                SamplingDuration = options.SamplingDuration,
                BreakDuration = options.BreakDuration,
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome.Exception)),
                StateProvider = health.Circuit,
                ManualControl = health.CircuitControl,
                OnOpened = args =>
                {
                    health.RecordCircuitOpened(timeProvider.GetUtcNow());
                    metrics.CircuitTransition(provider, "open");
                    LogCircuitOpened(logger, provider, args.BreakDuration.TotalSeconds, args.IsManual);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    metrics.CircuitTransition(provider, "half_open");
                    LogCircuitHalfOpened(logger, provider);
                    return default;
                },
                OnClosed = args =>
                {
                    metrics.CircuitTransition(provider, "closed");
                    LogCircuitClosed(logger, provider, args.IsManual);
                    return default;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions { Name = "attempt-timeout", Timeout = options.AttemptTimeout });
    }

    /// <summary>Technical failures. Business declines are results, not exceptions, so they never trip anything.</summary>
    public static bool IsTransient(Exception? exception) =>
        exception is ProviderUnavailableException or TimeoutRejectedException;

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Retrying {Provider} (attempt {Attempt}) in {DelayMs:F0} ms after {Error}")]
    private static partial void LogRetry(ILogger logger, string provider, int attempt, double delayMs, string? error);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Circuit for {Provider} OPENED for {BreakSeconds:F0} s (manual: {IsManual}); traffic fails over to the next eligible provider")]
    private static partial void LogCircuitOpened(ILogger logger, string provider, double breakSeconds, bool isManual);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Circuit for {Provider} is HALF-OPEN; letting a trial call through")]
    private static partial void LogCircuitHalfOpened(ILogger logger, string provider);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Circuit for {Provider} CLOSED (manual: {IsManual})")]
    private static partial void LogCircuitClosed(ILogger logger, string provider, bool isManual);
}
