using System.ComponentModel.DataAnnotations;
using Paygate.Domain.Payments;

namespace Paygate.Api.Contracts;

/// <summary>A player deposit (pay-in).</summary>
/// <param name="Amount">Amount in minor units of the currency, e.g. 2500 for 25.00 EUR.</param>
/// <param name="Currency">ISO-4217 code, e.g. EUR.</param>
/// <param name="Method">card, bank_transfer or crypto_wallet.</param>
/// <param name="Country">ISO 3166-1 alpha-2 country of the player, e.g. SE.</param>
/// <param name="CustomerId">The operator's player reference. Never personal data.</param>
/// <param name="PaymentToken">Token for the payment instrument from the hosted payment fields. Required for cards. Raw card numbers are never accepted.</param>
public sealed record CreateDepositRequest(
    [property: Range(1, 1_000_000_000_00)] long Amount,
    [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
    PaymentMethod Method,
    [property: Required, RegularExpression("^[A-Z]{2}$")] string Country,
    [property: Required, StringLength(64, MinimumLength = 1)] string CustomerId,
    [property: StringLength(128, MinimumLength = 8), RegularExpression("^tok_[A-Za-z0-9_]+$")] string? PaymentToken);

/// <summary>A payout (withdrawal) to the player.</summary>
/// <param name="Amount">Amount in minor units of the currency.</param>
/// <param name="Currency">ISO-4217 code.</param>
/// <param name="Method">card, bank_transfer or crypto_wallet.</param>
/// <param name="Country">ISO 3166-1 alpha-2 country of the player.</param>
/// <param name="CustomerId">The operator's player reference.</param>
/// <param name="DestinationToken">Token for the destination account or wallet, issued by the vault.</param>
public sealed record CreatePayoutRequest(
    [property: Range(1, 1_000_000_000_00)] long Amount,
    [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
    PaymentMethod Method,
    [property: Required, RegularExpression("^[A-Z]{2}$")] string Country,
    [property: Required, StringLength(64, MinimumLength = 1)] string CustomerId,
    [property: Required, StringLength(128, MinimumLength = 8), RegularExpression("^tok_[A-Za-z0-9_]+$")] string DestinationToken);

/// <param name="Amount">Amount to refund in minor units. Partial refunds are allowed until the captured amount is used up.</param>
/// <param name="Reason">Free-text reason kept for audit.</param>
public sealed record CreateRefundRequest(
    [property: Range(1, 1_000_000_000_00)] long Amount,
    [property: StringLength(256)] string? Reason);

public sealed record PaymentResponse(
    Guid Id,
    PaymentKind Kind,
    PaymentStatus Status,
    long Amount,
    string Currency,
    PaymentMethod Method,
    string Country,
    string CustomerId,
    string? Provider,
    string? ProviderReference,
    long RefundedAmount,
    FailureResponse? Failure,
    IReadOnlyList<AttemptResponse> Attempts,
    IReadOnlyList<RefundResponse> Refunds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PaymentResponse From(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return new PaymentResponse(
            payment.Id,
            payment.Kind,
            payment.Status,
            payment.Amount.MinorUnits,
            payment.Amount.Currency.Code,
            payment.Method,
            payment.Country,
            payment.CustomerId,
            payment.Provider,
            payment.ProviderReference,
            payment.RefundedAmount.MinorUnits,
            payment.FailureCode is null ? null : new FailureResponse(payment.FailureCode, payment.FailureMessage),
            payment.Attempts.OrderBy(a => a.Sequence).Select(a => new AttemptResponse(a.Provider, a.Outcome, a.DurationMs, a.Error)).ToList(),
            payment.Refunds.OrderBy(r => r.CreatedAt).Select(RefundResponse.From).ToList(),
            payment.CreatedAt,
            payment.UpdatedAt);
    }
}

public sealed record FailureResponse(string Code, string? Message);

public sealed record AttemptResponse(string Provider, AttemptOutcome Outcome, long DurationMs, string? Error);

public sealed record RefundResponse(Guid Id, long Amount, string Currency, string? Reason, string ProviderReference, DateTimeOffset CreatedAt)
{
    public static RefundResponse From(Refund refund)
    {
        ArgumentNullException.ThrowIfNull(refund);
        return new RefundResponse(refund.Id, refund.Amount.MinorUnits, refund.Amount.Currency.Code, refund.Reason, refund.ProviderReference, refund.CreatedAt);
    }
}

public sealed record RefundResultResponse(RefundResponse Refund, PaymentResponse Payment);

public sealed record PaymentListResponse(IReadOnlyList<PaymentResponse> Data, DateTimeOffset? NextCreatedBefore);
