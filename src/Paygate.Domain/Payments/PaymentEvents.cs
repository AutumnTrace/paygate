using Paygate.Domain.Common;

namespace Paygate.Domain.Payments;

/// <summary>Base for every event the <see cref="Payment"/> aggregate raises.</summary>
public abstract record PaymentEvent(Guid PaymentId, string MerchantId, DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public abstract string EventType { get; }
}

public sealed record PaymentCreated(
    Guid PaymentId,
    string MerchantId,
    PaymentKind Kind,
    PaymentMethod Method,
    long AmountMinor,
    string Currency,
    string Country,
    DateTimeOffset OccurredAt) : PaymentEvent(PaymentId, MerchantId, OccurredAt)
{
    public override string EventType => "payment.created.v1";
}

public sealed record PaymentStatusChanged(
    Guid PaymentId,
    string MerchantId,
    PaymentKind Kind,
    PaymentStatus From,
    PaymentStatus To,
    string? Provider,
    long AmountMinor,
    string Currency,
    DateTimeOffset OccurredAt) : PaymentEvent(PaymentId, MerchantId, OccurredAt)
{
    public override string EventType => "payment.status_changed.v1";
}

public sealed record PaymentRefunded(
    Guid PaymentId,
    string MerchantId,
    Guid RefundId,
    string Provider,
    long RefundAmountMinor,
    long TotalRefundedMinor,
    string Currency,
    DateTimeOffset OccurredAt) : PaymentEvent(PaymentId, MerchantId, OccurredAt)
{
    public override string EventType => "payment.refunded.v1";
}
