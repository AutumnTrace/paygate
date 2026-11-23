using Microsoft.EntityFrameworkCore;
using Paygate.Application.Payments;
using Paygate.Domain.Payments;

namespace Paygate.Infrastructure.Persistence;

internal sealed class EfPaymentRepository(PaygateDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetAsync(string merchantId, Guid id, CancellationToken cancellationToken) =>
        db.Payments.AsSplitQuery().SingleOrDefaultAsync(p => p.Id == id && p.MerchantId == merchantId, cancellationToken);

    public async Task<Payment?> FindForWebhookAsync(string provider, Guid? paymentId, string? providerReference, CancellationToken cancellationToken)
    {
        if (paymentId is { } id)
        {
            return await db.Payments.AsSplitQuery()
                .SingleOrDefaultAsync(p => p.Id == id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(providerReference))
        {
            return await db.Payments.AsSplitQuery()
                .SingleOrDefaultAsync(p => p.Provider == provider && p.ProviderReference == providerReference, cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    public async Task<IReadOnlyList<Payment>> ListAsync(string merchantId, DateTimeOffset? createdBefore, int limit, CancellationToken cancellationToken)
    {
        var query = db.Payments.AsNoTracking().AsSplitQuery().Where(p => p.MerchantId == merchantId);
        if (createdBefore is { } before)
        {
            query = query.Where(p => p.CreatedAt < before);
        }

        return await query.OrderByDescending(p => p.CreatedAt).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Add(Payment payment) => db.Payments.Add(payment);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The payment was changed by another request. Reload it and try again.", ex);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw new DuplicateKeyException("A row with the same key was saved concurrently.", ex);
        }
    }
}
