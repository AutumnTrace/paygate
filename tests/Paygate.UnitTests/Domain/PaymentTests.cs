using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.UnitTests.Domain;

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static Payment NewDeposit(long amount = 10_000, string currency = "EUR") =>
        Payment.Create(Guid.CreateVersion7(), "m_demo", PaymentKind.Deposit, PaymentMethod.Card, Money.Of(amount, currency), "SE", "player-42", Now);

    private static Payment CapturedDeposit(long amount = 10_000)
    {
        var payment = NewDeposit(amount);
        payment.MarkAccepted("heron", "hrn_1", PaymentStatus.Captured, Now);
        return payment;
    }

    [Fact]
    public void A_new_payment_starts_in_Created_and_raises_PaymentCreated()
    {
        var payment = NewDeposit();

        Assert.Equal(PaymentStatus.Created, payment.Status);
        Assert.Equal(Money.Of(0, "EUR"), payment.RefundedAmount);
        var created = Assert.IsType<PaymentCreated>(Assert.Single(payment.DomainEvents));
        Assert.Equal(10_000, created.AmountMinor);
        Assert.Equal("payment.created.v1", created.EventType);
    }

    [Theory]
    [InlineData(0, "SE", "player", "invalid_amount")]
    [InlineData(-5, "SE", "player", "invalid_amount")]
    [InlineData(100, "se", "player", "invalid_country")]
    [InlineData(100, "SWE", "player", "invalid_country")]
    [InlineData(100, "SE", " ", "invalid_customer_id")]
    [InlineData(100, "SE", "bad\nid", "invalid_customer_id")]
    public void Creation_validates_its_input(long amount, string country, string customerId, string expectedCode)
    {
        var ex = Assert.Throws<DomainException>(() => Payment.Create(
            Guid.NewGuid(), "m_demo", PaymentKind.Deposit, PaymentMethod.Card, Money.Of(amount, "EUR"), country, customerId, Now));

        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public void Accepting_records_the_provider_and_raises_a_status_change()
    {
        var payment = NewDeposit();
        payment.DequeueDomainEvents();

        payment.MarkAccepted("heron", "hrn_123", PaymentStatus.Captured, Now.AddSeconds(1));

        Assert.Equal(PaymentStatus.Captured, payment.Status);
        Assert.Equal("heron", payment.Provider);
        Assert.Equal("hrn_123", payment.ProviderReference);
        var changed = Assert.IsType<PaymentStatusChanged>(Assert.Single(payment.DomainEvents));
        Assert.Equal((PaymentStatus.Created, PaymentStatus.Captured), (changed.From, changed.To));
    }

    [Fact]
    public void Every_change_bumps_the_concurrency_version()
    {
        var payment = NewDeposit();
        var before = payment.Version;

        payment.RecordAttempt("heron", AttemptOutcome.TimedOut, TimeSpan.FromSeconds(2), "timeout", Now);
        payment.MarkAccepted("osprey", "osp_1", PaymentStatus.Pending, Now);

        Assert.Equal(before + 2, payment.Version);
    }

    [Fact]
    public void Attempts_are_kept_in_order()
    {
        var payment = NewDeposit();

        payment.RecordAttempt("heron", AttemptOutcome.TimedOut, TimeSpan.FromMilliseconds(2000), "timeout", Now);
        payment.RecordAttempt("osprey", AttemptOutcome.Accepted, TimeSpan.FromMilliseconds(80), null, Now);

        Assert.Collection(
            payment.Attempts,
            a => Assert.Equal((1, "heron", AttemptOutcome.TimedOut, 2000L), (a.Sequence, a.Provider, a.Outcome, a.DurationMs)),
            a => Assert.Equal((2, "osprey", AttemptOutcome.Accepted, 80L), (a.Sequence, a.Provider, a.Outcome, a.DurationMs)));
    }

    [Fact]
    public void Failing_stores_the_reason_and_is_terminal()
    {
        var payment = NewDeposit();

        payment.MarkFailed("declined", "insufficient_funds", Now);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("declined", payment.FailureCode);
        Assert.Throws<InvalidPaymentTransitionException>(() => payment.MarkAccepted("heron", "x", PaymentStatus.Captured, Now));
    }

    [Fact]
    public void Partial_then_full_refund()
    {
        var payment = CapturedDeposit(10_000);

        payment.Refund(Guid.NewGuid(), Money.Of(2_500, "EUR"), "goodwill", "rf_1", Now);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(Money.Of(7_500, "EUR"), payment.RefundableAmount);

        payment.Refund(Guid.NewGuid(), Money.Of(7_500, "EUR"), null, "rf_2", Now);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(Money.Of(10_000, "EUR"), payment.RefundedAmount);
        Assert.Equal(2, payment.Refunds.Count);
        Assert.Equal(2, payment.DomainEvents.OfType<PaymentRefunded>().Count());
    }

    [Fact]
    public void Cannot_refund_more_than_was_captured()
    {
        var payment = CapturedDeposit(10_000);
        payment.Refund(Guid.NewGuid(), Money.Of(9_000, "EUR"), null, "rf_1", Now);

        var ex = Assert.Throws<DomainException>(() => payment.Refund(Guid.NewGuid(), Money.Of(1_001, "EUR"), null, "rf_2", Now));

        Assert.Equal("refund_exceeds_captured_amount", ex.Code);
        Assert.Equal(Money.Of(9_000, "EUR"), payment.RefundedAmount);
    }

    [Fact]
    public void Cannot_refund_a_payment_that_was_not_captured() =>
        Assert.Equal(
            "payment_not_refundable",
            Assert.Throws<DomainException>(() => NewDeposit().EnsureRefundable(Money.Of(1, "EUR"))).Code);

    [Fact]
    public void Cannot_refund_in_another_currency() =>
        Assert.Equal(
            "currency_mismatch",
            Assert.Throws<DomainException>(() => CapturedDeposit().EnsureRefundable(Money.Of(1, "USD"))).Code);

    [Fact]
    public void Payouts_cannot_be_refunded()
    {
        var payout = Payment.Create(Guid.NewGuid(), "m_demo", PaymentKind.Payout, PaymentMethod.BankTransfer, Money.Of(500, "SEK"), "SE", "p1", Now);
        payout.MarkAccepted("osprey", "osp_9", PaymentStatus.Captured, Now);

        Assert.Equal("refund_not_supported", Assert.Throws<DomainException>(() => payout.EnsureRefundable(Money.Of(1, "SEK"))).Code);
    }

    [Fact]
    public void Webhook_moves_a_pending_payment_forward()
    {
        var payment = NewDeposit();
        payment.MarkAccepted("osprey", "osp_1", PaymentStatus.Pending, Now);

        var result = payment.ApplyProviderUpdate("osprey", PaymentStatus.Captured, null, Now);

        Assert.Equal(ProviderUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(PaymentStatus.Captured, payment.Status);
    }

    [Fact]
    public void Out_of_order_webhook_is_ignored_and_never_moves_backwards()
    {
        var payment = NewDeposit();
        payment.MarkAccepted("osprey", "osp_1", PaymentStatus.Pending, Now);
        payment.ApplyProviderUpdate("osprey", PaymentStatus.Captured, null, Now);

        // The "authorized" event was sent first but delivered last.
        var result = payment.ApplyProviderUpdate("osprey", PaymentStatus.Authorized, null, Now);

        Assert.Equal(ProviderUpdateOutcome.Stale, result.Outcome);
        Assert.Equal(PaymentStatus.Captured, payment.Status);
    }

    [Fact]
    public void Duplicate_webhook_is_reported_as_already_applied()
    {
        var payment = NewDeposit();
        payment.MarkAccepted("osprey", "osp_1", PaymentStatus.Pending, Now);
        payment.ApplyProviderUpdate("osprey", PaymentStatus.Captured, null, Now);

        Assert.Equal(ProviderUpdateOutcome.AlreadyApplied, payment.ApplyProviderUpdate("osprey", PaymentStatus.Captured, null, Now).Outcome);
    }

    [Fact]
    public void Webhook_from_a_provider_that_does_not_own_the_payment_is_ignored()
    {
        var payment = NewDeposit();
        payment.MarkAccepted("osprey", "osp_1", PaymentStatus.Pending, Now);

        var result = payment.ApplyProviderUpdate("heron", PaymentStatus.Captured, null, Now);

        Assert.Equal(ProviderUpdateOutcome.ProviderMismatch, result.Outcome);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public void Failed_webhook_keeps_the_provider_failure_code()
    {
        var payment = NewDeposit();
        payment.MarkAccepted("osprey", "osp_1", PaymentStatus.Pending, Now);

        payment.ApplyProviderUpdate("osprey", PaymentStatus.Failed, "bank_rejected", Now);

        Assert.Equal(("bank_rejected", PaymentStatus.Failed), (payment.FailureCode, payment.Status));
    }

    [Property(MaxTest = 300)]
    public Property Refunds_never_exceed_the_captured_amount(PositiveInt amount, PositiveInt[] refunds)
    {
        var payment = CapturedDeposit(amount.Get);

        foreach (var refund in refunds)
        {
            try
            {
                payment.Refund(Guid.NewGuid(), Money.Of(refund.Get, "EUR"), null, "rf", Now);
            }
            catch (DomainException)
            {
                // Rejected refunds must leave the payment unchanged; the invariant below checks that.
            }
        }

        var total = payment.Refunds.Sum(r => r.Amount.MinorUnits);
        return (payment.RefundedAmount.MinorUnits <= payment.Amount.MinorUnits).Label("refunded <= amount")
            .And(total == payment.RefundedAmount.MinorUnits).Label("refund rows add up")
            .And((payment.Status == PaymentStatus.Refunded) == (total == amount.Get)).Label("fully refunded iff status Refunded");
    }

    [Property(MaxTest = 300)]
    public bool Any_sequence_of_webhooks_leaves_the_payment_in_a_reachable_state(byte[] updates)
    {
        PaymentStatus[] reportable = [PaymentStatus.Authorized, PaymentStatus.Captured, PaymentStatus.Failed, PaymentStatus.Expired];
        var payment = NewDeposit();
        payment.MarkAccepted("osprey", "osp_1", PaymentStatus.Pending, Now);
        var previous = payment.Status;

        foreach (var update in updates)
        {
            payment.ApplyProviderUpdate("osprey", reportable[update % reportable.Length], null, Now);

            // Never moves backwards: either it stayed put or it took a legal step.
            if (payment.Status != previous && !PaymentStateMachine.CanTransition(previous, payment.Status))
            {
                return false;
            }

            previous = payment.Status;
        }

        return true;
    }
}
