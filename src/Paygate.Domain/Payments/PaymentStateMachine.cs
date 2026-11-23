using System.Collections.Frozen;

namespace Paygate.Domain.Payments;

/// <summary>
/// The single source of truth for which status changes are legal.
/// <code>
/// Created -> Pending -> Authorized -> Captured -> PartiallyRefunded -> Refunded
///    |          |           |             \-----------------------------^
///    +----------+-----------+--> Failed / Expired
/// </code>
/// A provider may also skip steps, for example Created -> Captured for an instant card capture.
/// </summary>
public static class PaymentStateMachine
{
    private static readonly FrozenDictionary<PaymentStatus, PaymentStatus[]> Transitions =
        new Dictionary<PaymentStatus, PaymentStatus[]>
        {
            [PaymentStatus.Created] = [PaymentStatus.Pending, PaymentStatus.Authorized, PaymentStatus.Captured, PaymentStatus.Failed],
            [PaymentStatus.Pending] = [PaymentStatus.Authorized, PaymentStatus.Captured, PaymentStatus.Failed, PaymentStatus.Expired],
            [PaymentStatus.Authorized] = [PaymentStatus.Captured, PaymentStatus.Failed, PaymentStatus.Expired],
            [PaymentStatus.Captured] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
            [PaymentStatus.PartiallyRefunded] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
            [PaymentStatus.Refunded] = [],
            [PaymentStatus.Failed] = [],
            [PaymentStatus.Expired] = [],
        }.ToFrozenDictionary();

    public static bool CanTransition(PaymentStatus from, PaymentStatus to) =>
        Transitions.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0;

    public static IReadOnlyList<PaymentStatus> AllowedFrom(PaymentStatus from) =>
        Transitions.TryGetValue(from, out var allowed) ? allowed : [];

    public static bool IsTerminal(PaymentStatus status) => AllowedFrom(status).Count == 0;

    /// <summary>True for states where money has actually moved.</summary>
    public static bool IsSettled(PaymentStatus status) =>
        status is PaymentStatus.Captured or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded;
}
