using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paygate.Application.Events;
using Paygate.Infrastructure.Persistence;

namespace Paygate.Infrastructure.Events;

/// <summary>
/// Publishes outbox messages in insertion order and marks them published. Delivery is at-least-once:
/// if the process dies after publishing but before marking, the batch is published again, and
/// consumers deduplicate on the event id. A failing broker only delays events; payments carry on.
/// </summary>
internal sealed partial class OutboxDispatcher(
    IDbContextFactory<PaygateDbContext> contexts,
    IEventPublisher publisher,
    IOptions<EventsOptions> options,
    TimeProvider time,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollInterval, time);
        do
        {
            try
            {
                // Drain full batches back to back; wait for the timer once the outbox is caught up.
                while (await DispatchBatchAsync(stoppingToken).ConfigureAwait(false) == options.Value.BatchSize)
                {
                }

                await PurgePublishedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDispatchFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Publishes one batch. Returns how many messages were published.</summary>
    internal async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var batch = await db.OutboxMessages
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.Id)
            .Take(options.Value.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (batch.Count == 0)
        {
            return 0;
        }

        try
        {
            await publisher.PublishAsync(batch.Select(m => m.ToEnvelope()).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var message in batch)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 512 ? ex.Message[..512] : ex.Message;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            LogPublishFailed(logger, ex, batch.Count, batch[0].Attempts);
            return 0;
        }

        var now = time.GetUtcNow();
        foreach (var message in batch)
        {
            message.PublishedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogPublished(logger, batch.Count);
        return batch.Count;
    }

    private async Task PurgePublishedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now - _lastPurge < PurgeEvery)
        {
            return;
        }

        _lastPurge = now;
        var cutoff = now - options.Value.PublishedRetention;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.OutboxMessages.Where(m => m.PublishedAt != null && m.PublishedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Debug, Message = "Published {Count} outbox message(s)")]
    private static partial void LogPublished(ILogger logger, int count);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Publishing {Count} outbox message(s) failed (attempt {Attempt}); will retry")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, int count, int attempt);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Error, Message = "Outbox dispatch loop failed; will retry on the next tick")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception);
}
