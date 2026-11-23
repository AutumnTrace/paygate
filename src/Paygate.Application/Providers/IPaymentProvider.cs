using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.Application.Providers;

/// <summary>
/// A payment service provider (PSP) adapter. Implementations translate these calls to one PSP's API.
/// <para>
/// Contract: a <b>business decline</b> is a normal return value (<see cref="ProviderResponseStatus.Declined"/>),
/// because retrying or failing over a declined card would only produce more declines.
/// A <b>technical failure</b> is an exception (<see cref="ProviderUnavailableException"/>, or a timeout raised
/// by the resilience pipeline), which the router may retry or fail over.
/// </para>
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Stable lower-case identifier used in routing rules, metrics and webhook URLs.</summary>
    string Name { get; }

    string DisplayName { get; }

    ProviderCapabilities Capabilities { get; }

    /// <summary>Sends a deposit or payout to the provider.</summary>
    Task<ProviderResponse> ProcessAsync(ProviderRequest request, CancellationToken cancellationToken);

    Task<ProviderRefundResponse> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken);
}

public sealed record ProviderRequest(
    Guid PaymentId,
    string IdempotencyKey,
    PaymentKind Kind,
    PaymentMethod Method,
    Money Amount,
    string Country,
    string CustomerId,
    string? InstrumentToken);

public enum ProviderResponseStatus
{
    Captured,
    Authorized,

    /// <summary>Accepted; the final outcome arrives later by webhook (bank transfers, crypto confirmations).</summary>
    Pending,

    Declined,
}

public sealed record ProviderResponse(ProviderResponseStatus Status, string? ProviderReference, string? DeclineCode = null)
{
    public static ProviderResponse Declined(string code) => new(ProviderResponseStatus.Declined, null, code);
}

public sealed record ProviderRefundRequest(Guid RefundId, string IdempotencyKey, string ProviderReference, Money Amount);

public sealed record ProviderRefundResponse(string RefundReference);

/// <summary>The provider could not process the request for a technical reason (5xx, connection reset, maintenance).</summary>
public sealed class ProviderUnavailableException(string provider, string message, Exception? innerException = null)
    : Exception($"{provider}: {message}", innerException)
{
    public string Provider { get; } = provider;
}
