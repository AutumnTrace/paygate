using Paygate.Domain.Common;

namespace Paygate.Domain.Payments;

/// <summary>
/// A deposit or payout and everything that happened to it. All status changes go through
/// <see cref="PaymentStateMachine"/>, so an illegal move (for example refunding a failed
/// payment) is impossible rather than just discouraged.
/// </summary>
public sealed class Payment : AggregateRoot
{
    public const int MaxCustomerIdLength = 64;

    private readonly List<PaymentAttempt> _attempts = [];
    private readonly List<Refund> _refunds = [];

    private Payment()
    {
        MerchantId = string.Empty;
        Amount = null!;
        RefundedAmount = null!;
        Country = string.Empty;
        CustomerId = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>The operator (tenant) that owns this payment.</summary>
    public string MerchantId { get; private set; }

    public PaymentKind Kind { get; private set; }

    public PaymentMethod Method { get; private set; }

    public Money Amount { get; private set; }

    public Money RefundedAmount { get; private set; }

    /// <summary>ISO 3166-1 alpha-2 country of the player.</summary>
    public string Country { get; private set; }

    /// <summary>The operator's reference for the player. Never personal data.</summary>
    public string CustomerId { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>The provider that accepted the payment, once one has.</summary>
    public string? Provider { get; private set; }

    public string? ProviderReference { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token. Incremented on every change.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<PaymentAttempt> Attempts => _attempts;

    public IReadOnlyList<Refund> Refunds => _refunds;

    /// <summary>How much of a captured deposit can still be refunded.</summary>
    public Money RefundableAmount =>
        Kind == PaymentKind.Deposit && Status is PaymentStatus.Captured or PaymentStatus.PartiallyRefunded
            ? Amount - RefundedAmount
            : Money.Zero(Amount.Currency);

    public static Payment Create(
        Guid id,
        string merchantId,
        PaymentKind kind,
        PaymentMethod method,
        Money amount,
        string country,
        string customerId,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantId);
        ArgumentNullException.ThrowIfNull(amount);

        if (!amount.IsPositive)
        {
            throw new DomainException("invalid_amount", "The amount must be greater than zero.");
        }

        if (!IsCountryCode(country))
        {
            throw new DomainException("invalid_country", "The country must be an ISO 3166-1 alpha-2 code, e.g. \"SE\".");
        }

        if (string.IsNullOrWhiteSpace(customerId) || customerId.Length > MaxCustomerIdLength || customerId.Any(char.IsControl))
        {
            throw new DomainException(
                "invalid_customer_id",
                $"The customer id must be 1 to {MaxCustomerIdLength} printable characters.");
        }

        var payment = new Payment
        {
            Id = id,
            MerchantId = merchantId,
            Kind = kind,
            Method = method,
            Amount = amount,
            RefundedAmount = Money.Zero(amount.Currency),
            Country = country,
            CustomerId = customerId,
            Status = PaymentStatus.Created,
            CreatedAt = now,
            UpdatedAt = now,
        };

        payment.Raise(new PaymentCreated(id, merchantId, kind, method, amount.MinorUnits, amount.Currency.Code, country, now));
        return payment;
    }

    /// <summary>Records one provider call made by the router, in order.</summary>
    public void RecordAttempt(string provider, AttemptOutcome outcome, TimeSpan duration, string? error, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _attempts.Add(new PaymentAttempt(_attempts.Count + 1, provider, outcome, duration, error, now));
        Touch(now);
    }

    /// <summary>A provider accepted the payment and reported its first status.</summary>
    public void MarkAccepted(string provider, string providerReference, PaymentStatus status, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);

        if (status is not (PaymentStatus.Pending or PaymentStatus.Authorized or PaymentStatus.Captured))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A provider can only accept as Pending, Authorized or Captured.");
        }

        Provider = provider;
        ProviderReference = providerReference;
        TransitionTo(status, now);
    }

    public void MarkFailed(string code, string message, DateTimeOffset now)
    {
        TransitionTo(PaymentStatus.Failed, now);
        FailureCode = code;
        FailureMessage = message;
    }

    public void Expire(DateTimeOffset now) => TransitionTo(PaymentStatus.Expired, now);

    /// <summary>Checks a refund request against the business rules without changing anything.</summary>
    public void EnsureRefundable(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (Kind != PaymentKind.Deposit)
        {
            throw new DomainException("refund_not_supported", "Only deposits can be refunded.");
        }

        if (Status is not (PaymentStatus.Captured or PaymentStatus.PartiallyRefunded))
        {
            throw new DomainException("payment_not_refundable", $"A {Status} payment cannot be refunded; it must be captured first.");
        }

        if (amount.Currency != Amount.Currency)
        {
            throw new DomainException("currency_mismatch", $"The refund must be in {Amount.Currency.Code}, the payment's currency.");
        }

        if (!amount.IsPositive)
        {
            throw new DomainException("invalid_amount", "The refund amount must be greater than zero.");
        }

        if (amount > RefundableAmount)
        {
            throw new DomainException(
                "refund_exceeds_captured_amount",
                $"Cannot refund {amount}: only {RefundableAmount} of {Amount} is still refundable.");
        }
    }

    /// <summary>Applies a refund the provider has confirmed.</summary>
    public Refund Refund(Guid refundId, Money amount, string? reason, string providerReference, DateTimeOffset now)
    {
        EnsureRefundable(amount);

        var refund = new Refund(refundId, amount, reason, providerReference, now);
        _refunds.Add(refund);
        RefundedAmount += amount;

        TransitionTo(RefundedAmount == Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded, now);
        Raise(new PaymentRefunded(
            Id, MerchantId, refundId, Provider!, amount.MinorUnits, RefundedAmount.MinorUnits, Amount.Currency.Code, now));
        return refund;
    }

    /// <summary>
    /// Applies an asynchronous status update from a provider (a webhook). Webhooks arrive late,
    /// twice or out of order, so this never throws for those cases: an update that is not a
    /// legal forward move from the current status is acknowledged and ignored.
    /// </summary>
    public ProviderUpdateResult ApplyProviderUpdate(string provider, PaymentStatus reported, string? failureCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        if (reported is not (PaymentStatus.Authorized or PaymentStatus.Captured or PaymentStatus.Failed or PaymentStatus.Expired))
        {
            throw new ArgumentOutOfRangeException(nameof(reported), reported, "Providers can only report Authorized, Captured, Failed or Expired.");
        }

        if (Provider is null || !string.Equals(Provider, provider, StringComparison.Ordinal))
        {
            return new ProviderUpdateResult(
                ProviderUpdateOutcome.ProviderMismatch,
                $"The payment is not routed to '{provider}' (current provider: {Provider ?? "none"}).");
        }

        if (reported == Status)
        {
            return new ProviderUpdateResult(ProviderUpdateOutcome.AlreadyApplied, $"The payment is already {Status}.");
        }

        if (!PaymentStateMachine.CanTransition(Status, reported))
        {
            return new ProviderUpdateResult(
                ProviderUpdateOutcome.Stale,
                $"Ignored {reported}: the payment is already {Status}, and payments never move backwards.");
        }

        if (reported == PaymentStatus.Failed)
        {
            MarkFailed(failureCode ?? "provider_failed", "The provider reported the payment as failed.", now);
        }
        else
        {
            TransitionTo(reported, now);
        }

        return new ProviderUpdateResult(ProviderUpdateOutcome.Applied, $"The payment is now {Status}.");
    }

    private void TransitionTo(PaymentStatus to, DateTimeOffset now)
    {
        if (!PaymentStateMachine.CanTransition(Status, to))
        {
            throw new InvalidPaymentTransitionException(Status, to);
        }

        var from = Status;
        Status = to;
        Touch(now);
        Raise(new PaymentStatusChanged(Id, MerchantId, Kind, from, to, Provider, Amount.MinorUnits, Amount.Currency.Code, now));
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private static bool IsCountryCode(string? value) =>
        value is { Length: 2 } && char.IsAsciiLetterUpper(value[0]) && char.IsAsciiLetterUpper(value[1]);
}

public enum ProviderUpdateOutcome
{
    Applied,
    AlreadyApplied,

    /// <summary>The update arrived out of order (or conflicts) and was ignored.</summary>
    Stale,

    /// <summary>The update came from a provider that does not own this payment.</summary>
    ProviderMismatch,
}

public sealed record ProviderUpdateResult(ProviderUpdateOutcome Outcome, string Detail);
