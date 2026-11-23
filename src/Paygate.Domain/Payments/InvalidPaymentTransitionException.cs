using Paygate.Domain.Common;

namespace Paygate.Domain.Payments;

public sealed class InvalidPaymentTransitionException(PaymentStatus from, PaymentStatus to)
    : DomainException(
        "invalid_status_transition",
        $"A payment cannot move from {from} to {to}. Allowed: {Describe(PaymentStateMachine.AllowedFrom(from))}.")
{
    public PaymentStatus From { get; } = from;

    public PaymentStatus To { get; } = to;

    private static string Describe(IReadOnlyList<PaymentStatus> allowed) =>
        allowed.Count == 0 ? "none (terminal state)" : string.Join(", ", allowed);
}
