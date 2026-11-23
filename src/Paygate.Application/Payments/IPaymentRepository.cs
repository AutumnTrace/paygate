using Paygate.Domain.Payments;

namespace Paygate.Application.Payments;

/// <summary>Persistence port for the <see cref="Payment"/> aggregate (attempts and refunds included).</summary>
public interface IPaymentRepository
{
    /// <summary>Loads a payment owned by <paramref name="merchantId"/>. Other merchants' payments are invisible.</summary>
    Task<Payment?> GetAsync(string merchantId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the payment a provider webhook refers to: by our payment id when the provider echoes it
    /// back, otherwise by the provider's own reference. Not scoped to a merchant.
    /// </summary>
    Task<Payment?> FindForWebhookAsync(string provider, Guid? paymentId, string? providerReference, CancellationToken cancellationToken);

    /// <summary>Newest first, using the (merchant_id, created_at) index.</summary>
    Task<IReadOnlyList<Payment>> ListAsync(string merchantId, DateTimeOffset? createdBefore, int limit, CancellationToken cancellationToken);

    void Add(Payment payment);

    /// <summary>
    /// Saves everything in the unit of work in one transaction. Throws <see cref="ConcurrencyConflictException"/>
    /// if another request changed the payment first, and <see cref="DuplicateKeyException"/> on a unique-key race.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Optimistic concurrency check failed: the payment changed since it was loaded.</summary>
public sealed class ConcurrencyConflictException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>A row with the same unique key was committed concurrently.</summary>
public sealed class DuplicateKeyException(string message, Exception? innerException = null) : Exception(message, innerException);
