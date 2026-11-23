using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paygate.Application.Idempotency;
using Paygate.Infrastructure.Persistence;

namespace Paygate.Infrastructure.Idempotency;

/// <summary>
/// Idempotency keys in the main database. "First request wins" is enforced by the primary key on
/// (merchant_id, key): two concurrent requests both try to INSERT, exactly one succeeds, and the
/// loser reads the winner's row to decide between replay (409 while in flight) and mismatch.
/// Each operation uses its own short-lived DbContext so it never mixes with the business unit of work.
/// </summary>
internal sealed class EfIdempotencyStore(
    IDbContextFactory<PaygateDbContext> contexts,
    IOptions<IdempotencyOptions> options,
    TimeProvider time) : IIdempotencyStore
{
    private readonly IdempotencyOptions _options = options.Value;

    public async Task<IdempotencyDecision> BeginAsync(IdempotencyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Two rounds at most: the second one only happens when the existing key was expired or just released.
        for (var round = 0; round < 2; round++)
        {
            var now = time.GetUtcNow();
            await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            // Retries of a finished request are the common case, so look first; the INSERT (and its
            // unique constraint) only has to settle genuine races between concurrent requests.
            var existing = await FindAsync(db, request, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                if (await TryInsertAsync(request, now, cancellationToken).ConfigureAwait(false))
                {
                    return IdempotencyDecision.Proceed;
                }

                existing = await FindAsync(db, request, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    continue; // Released between our INSERT and SELECT; try to claim it again.
                }
            }

            if (existing.ExpiresAt <= now)
            {
                await db.IdempotencyRecords
                    .Where(r => r.MerchantId == request.MerchantId && r.Key == request.Key && r.ExpiresAt == existing.ExpiresAt)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(existing.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
            {
                return IdempotencyDecision.Mismatch;
            }

            if (existing.Status == IdempotencyRecordStatus.Completed)
            {
                return new IdempotencyDecision(
                    IdempotencyOutcome.Replay,
                    new StoredResponse(existing.ResponseStatusCode ?? 200, existing.ResponseContentType, existing.ResponseLocation, existing.ResponseBody ?? []));
            }

            if (existing.LockedUntil <= now)
            {
                // The request that held the key died without finishing. Take the key over atomically:
                // only one waiting request can match the old LockedUntil value.
                var taken = await db.IdempotencyRecords
                    .Where(r => r.MerchantId == request.MerchantId && r.Key == request.Key
                        && r.Status == IdempotencyRecordStatus.InFlight && r.LockedUntil == existing.LockedUntil)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.LockedUntil, now + _options.InFlightLockTimeout), cancellationToken)
                    .ConfigureAwait(false);
                return taken == 1 ? IdempotencyDecision.Proceed : IdempotencyDecision.InFlight;
            }

            return IdempotencyDecision.InFlight;
        }

        return IdempotencyDecision.InFlight;
    }

    public async Task CompleteAsync(string merchantId, string key, StoredResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var now = time.GetUtcNow();

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.IdempotencyRecords
            .Where(r => r.MerchantId == merchantId && r.Key == key && r.Status == IdempotencyRecordStatus.InFlight)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, IdempotencyRecordStatus.Completed)
                    .SetProperty(r => r.ResponseStatusCode, response.StatusCode)
                    .SetProperty(r => r.ResponseContentType, response.ContentType)
                    .SetProperty(r => r.ResponseLocation, response.Location)
                    .SetProperty(r => r.ResponseBody, response.Body)
                    .SetProperty(r => r.CompletedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReleaseAsync(string merchantId, string key, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.IdempotencyRecords
            .Where(r => r.MerchantId == merchantId && r.Key == key && r.Status == IdempotencyRecordStatus.InFlight)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.IdempotencyRecords.Where(r => r.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<IdempotencyRecord?> FindAsync(PaygateDbContext db, IdempotencyRequest request, CancellationToken cancellationToken) =>
        db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.MerchantId == request.MerchantId && r.Key == request.Key, cancellationToken);

    private async Task<bool> TryInsertAsync(IdempotencyRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            MerchantId = request.MerchantId,
            Key = request.Key,
            Fingerprint = request.Fingerprint,
            Method = request.Method,
            Path = request.Path.Length > 256 ? request.Path[..256] : request.Path,
            Status = IdempotencyRecordStatus.InFlight,
            CreatedAt = now,
            LockedUntil = now + _options.InFlightLockTimeout,
            ExpiresAt = now + _options.RetentionPeriod,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return false;
        }
    }
}
