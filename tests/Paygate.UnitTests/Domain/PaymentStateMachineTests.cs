using Paygate.Domain.Payments;

namespace Paygate.UnitTests.Domain;

public sealed class PaymentStateMachineTests
{
    [Theory]
    [InlineData(PaymentStatus.Created, PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Created, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Expired)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.PartiallyRefunded)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.PartiallyRefunded, PaymentStatus.PartiallyRefunded)]
    [InlineData(PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded)]
    public void Allows_forward_transitions(PaymentStatus from, PaymentStatus to) =>
        Assert.True(PaymentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Created, PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.PartiallyRefunded)]
    [InlineData(PaymentStatus.Failed, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Refunded, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Expired, PaymentStatus.Captured)]
    public void Rejects_backward_and_skipping_transitions(PaymentStatus from, PaymentStatus to) =>
        Assert.False(PaymentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Expired)]
    public void Terminal_states_have_no_exits(PaymentStatus status)
    {
        Assert.True(PaymentStateMachine.IsTerminal(status));
        Assert.Empty(PaymentStateMachine.AllowedFrom(status));
    }

    [Fact]
    public void Every_status_is_covered_by_the_transition_table()
    {
        foreach (var status in Enum.GetValues<PaymentStatus>())
        {
            var isTerminal = status is PaymentStatus.Refunded or PaymentStatus.Failed or PaymentStatus.Expired;
            Assert.Equal(isTerminal, PaymentStateMachine.IsTerminal(status));
        }
    }

    [Fact]
    public void Every_non_terminal_state_can_reach_a_terminal_state()
    {
        foreach (var start in Enum.GetValues<PaymentStatus>())
        {
            var seen = new HashSet<PaymentStatus>();
            var queue = new Queue<PaymentStatus>([start]);
            var reachesTerminal = false;

            while (queue.TryDequeue(out var current))
            {
                if (!seen.Add(current))
                {
                    continue;
                }

                reachesTerminal |= PaymentStateMachine.IsTerminal(current);
                foreach (var next in PaymentStateMachine.AllowedFrom(current))
                {
                    queue.Enqueue(next);
                }
            }

            Assert.True(reachesTerminal, $"{start} can never finish");
        }
    }
}
