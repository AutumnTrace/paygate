using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Paygate.Application.Providers;
using Paygate.Application.Resilience;
using Paygate.Application.Routing;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.Application.Payments;

public sealed record CreatePaymentCommand(
    string MerchantId,
    PaymentKind Kind,
    PaymentMethod Method,
    long Amount,
    string Currency,
    string Country,
    string CustomerId,
    string? InstrumentToken);

public sealed record RefundCommand(string MerchantId, Guid PaymentId, long Amount, string? Reason, string IdempotencyKey);

/// <summary>A refund could not be confirmed by the provider. Safe to retry with the same Idempotency-Key.</summary>
public sealed class RefundFailedException(string provider, string reason)
    : Exception($"{provider} did not confirm the refund: {reason}.")
{
    public string Provider { get; } = provider;
}

/// <summary>Application service behind the payment endpoints.</summary>
public sealed partial class PaymentService(
    IPaymentRepository payments,
    PaymentRouter router,
    ProviderCatalog providers,
    ProviderInvoker invoker,
    TimeProvider time,
    ILogger<PaymentService> logger)
{
    public async Task<Payment> CreateAsync(CreatePaymentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var payment = Payment.Create(
            Guid.CreateVersion7(),
            command.MerchantId,
            command.Kind,
            command.Method,
            new Money(command.Amount, Currency.FromCode(command.Currency)),
            command.Country,
            command.CustomerId,
            time.GetUtcNow());

        // Persist before calling any provider: if the process dies mid-call, the payment still
        // exists (as Created) and can be reconciled, instead of money moving with no record of it.
        payments.Add(payment);
        await payments.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // From here on the client's cancellation token is deliberately not used. A player closing the
        // tab must not abandon a half-finished PSP call; the result is stored under the Idempotency-Key
        // and the resilience pipeline's total timeout bounds how long this can take.
        await router.RouteAsync(payment, command.InstrumentToken, CancellationToken.None).ConfigureAwait(false);
        await payments.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return payment;
    }

    public Task<Payment?> GetAsync(string merchantId, Guid id, CancellationToken cancellationToken) =>
        payments.GetAsync(merchantId, id, cancellationToken);

    public Task<IReadOnlyList<Payment>> ListAsync(string merchantId, DateTimeOffset? createdBefore, int limit, CancellationToken cancellationToken) =>
        payments.ListAsync(merchantId, createdBefore, Math.Clamp(limit, 1, 100), cancellationToken);

    /// <summary>
    /// Refunds go back to the provider that captured the money; there is no failover for them.
    /// The provider idempotency key is derived from the client's Idempotency-Key, so a client retry
    /// after a timeout reaches the provider with the same key and cannot refund twice.
    /// </summary>
    public async Task<(Payment Payment, Refund Refund)?> RefundAsync(RefundCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var payment = await payments.GetAsync(command.MerchantId, command.PaymentId, cancellationToken).ConfigureAwait(false);
        if (payment is null)
        {
            return null;
        }

        var amount = new Money(command.Amount, payment.Amount.Currency);
        payment.EnsureRefundable(amount);

        var provider = providers.Get(payment.Provider!);
        if (!provider.Capabilities.SupportsRefunds)
        {
            throw new DomainException("refund_not_supported", $"{provider.DisplayName} does not support refunds; settle this one off-platform.");
        }

        var refundId = Guid.CreateVersion7();
        var request = new ProviderRefundRequest(refundId, ProviderRefundKey(command), payment.ProviderReference!, amount);
        var result = await invoker.RefundAsync(provider, request, CancellationToken.None).ConfigureAwait(false);

        if (result.Outcome != AttemptOutcome.Accepted)
        {
            LogRefundFailed(logger, payment.Id, provider.Name, result.Error ?? result.Outcome.ToString());
            throw new RefundFailedException(provider.Name, result.Error ?? "unavailable");
        }

        var refund = payment.Refund(refundId, amount, command.Reason, result.Response!.RefundReference, time.GetUtcNow());
        await payments.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        return (payment, refund);
    }

    private static string ProviderRefundKey(RefundCommand command)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{command.MerchantId}\n{command.PaymentId:N}\n{command.IdempotencyKey}"));
        return "pg_rf_" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Refund for payment {PaymentId} failed at {Provider}: {Error}")]
    private static partial void LogRefundFailed(ILogger logger, Guid paymentId, string provider, string error);
}
