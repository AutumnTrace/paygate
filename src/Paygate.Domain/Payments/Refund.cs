using Paygate.Domain.Common;

namespace Paygate.Domain.Payments;

/// <summary>A full or partial refund of a captured deposit. Always sent to the provider that captured it.</summary>
public sealed class Refund
{
    private Refund()
    {
        Amount = null!;
        ProviderReference = string.Empty;
    }

    internal Refund(Guid id, Money amount, string? reason, string providerReference, DateTimeOffset createdAt)
    {
        Id = id;
        Amount = amount;
        Reason = reason;
        ProviderReference = providerReference;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Money Amount { get; private set; }

    public string? Reason { get; private set; }

    public string ProviderReference { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
