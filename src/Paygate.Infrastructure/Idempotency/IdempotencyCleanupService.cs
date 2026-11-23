using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paygate.Application.Idempotency;

namespace Paygate.Infrastructure.Idempotency;

/// <summary>Deletes expired idempotency keys on a timer, using the index on expires_at.</summary>
internal sealed partial class IdempotencyCleanupService(
    IIdempotencyStore store,
    IOptions<IdempotencyOptions> options,
    TimeProvider time,
    ILogger<IdempotencyCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var removed = await store.PurgeExpiredAsync(stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    LogPurged(logger, removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPurgeFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Purged {Count} expired idempotency keys")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Purging expired idempotency keys failed; will retry on the next tick")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
