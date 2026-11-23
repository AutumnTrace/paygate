using Microsoft.Extensions.Logging;
using Paygate.Application.Observability;
using Paygate.Application.Payments;
using Paygate.Domain.Payments;

namespace Paygate.Application.Webhooks;

/// <summary>
/// The JSON body a provider posts. <c>Id</c> is the provider's event id: it is inside the signed body,
/// so it cannot be swapped to slip a replay past deduplication. Unknown fields are ignored.
/// </summary>
public sealed record WebhookPayload(string Id, string Type, Guid? PaymentId, string? ProviderReference, string? FailureCode, DateTimeOffset? OccurredAt);

public enum WebhookOutcome
{
    Applied,
    AlreadyApplied,

    /// <summary>Arrived out of order (older than the current status) and was ignored.</summary>
    Stale,

    /// <summary>A provider reported on a payment another provider owns: a possible double charge.</summary>
    ProviderMismatch,

    /// <summary>This event id was processed before.</summary>
    Duplicate,

    /// <summary>An event type the gateway does not act on.</summary>
    Ignored,
}

public sealed record WebhookResult(string EventId, WebhookOutcome Outcome, Guid? PaymentId, PaymentStatus? PaymentStatus, string Detail);

/// <summary>Remembers processed webhook event ids, in the same transaction as the payment change.</summary>
public interface IWebhookInbox
{
    Task<bool> ContainsAsync(string provider, string eventId, CancellationToken cancellationToken);

    /// <summary>Stages the event; it is saved with the next <see cref="IPaymentRepository.SaveChangesAsync"/>.</summary>
    void Add(string provider, string eventId, string type, Guid? paymentId, WebhookOutcome outcome, DateTimeOffset receivedAt);
}

/// <summary>The payment a webhook refers to does not exist (yet). The provider should retry.</summary>
public sealed class WebhookPaymentNotFoundException(string message) : Exception(message);

/// <summary>The payment exists but its own provider call has not finished recording. The provider should retry.</summary>
public sealed class WebhookPaymentNotReadyException(string message) : Exception(message);

/// <summary>
/// Applies provider webhooks. Three defences keep this safe:
/// signatures are checked before this runs; each event id is processed once (the inbox row and the
/// payment change commit together, so a crash cannot apply an event without remembering it); and the
/// payment state machine ignores updates that arrive out of order instead of moving backwards.
/// </summary>
public sealed partial class WebhookService(
    IPaymentRepository payments,
    IWebhookInbox inbox,
    PaygateMetrics metrics,
    TimeProvider time,
    ILogger<WebhookService> logger)
{
    public async Task<WebhookResult> HandleAsync(string provider, WebhookPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var eventId = payload.Id;

        if (await inbox.ContainsAsync(provider, eventId, cancellationToken).ConfigureAwait(false))
        {
            return Record(provider, new WebhookResult(eventId, WebhookOutcome.Duplicate, payload.PaymentId, null, "Already processed; nothing changed."));
        }

        var now = time.GetUtcNow();
        if (ToStatus(payload.Type) is not { } reported)
        {
            inbox.Add(provider, eventId, payload.Type, payload.PaymentId, WebhookOutcome.Ignored, now);
            await SaveOnceAsync(cancellationToken).ConfigureAwait(false);
            return Record(provider, new WebhookResult(eventId, WebhookOutcome.Ignored, payload.PaymentId, null, $"Event type '{payload.Type}' is not used by the gateway."));
        }

        var payment = await payments.FindForWebhookAsync(provider, payload.PaymentId, payload.ProviderReference, cancellationToken).ConfigureAwait(false)
            ?? throw new WebhookPaymentNotFoundException("No payment matches this webhook's paymentId or providerReference.");

        if (payment.Status == PaymentStatus.Created)
        {
            // Our own call to this provider is still being recorded. Acknowledging now would lose the update.
            throw new WebhookPaymentNotReadyException($"Payment {payment.Id} is still being routed. Redeliver this event shortly.");
        }

        var update = payment.ApplyProviderUpdate(provider, reported, payload.FailureCode, now, payload.ProviderReference);
        var outcome = update.Outcome switch
        {
            ProviderUpdateOutcome.Applied => WebhookOutcome.Applied,
            ProviderUpdateOutcome.AlreadyApplied => WebhookOutcome.AlreadyApplied,
            ProviderUpdateOutcome.Stale => WebhookOutcome.Stale,
            _ => WebhookOutcome.ProviderMismatch,
        };

        if (outcome == WebhookOutcome.ProviderMismatch && reported is PaymentStatus.Captured or PaymentStatus.Authorized)
        {
            // Typical cause: the gateway timed out on this provider, failed over, and the first provider
            // charged anyway. Someone has to reverse one of the two charges.
            LogPossibleDoubleCharge(logger, payment.Id, provider, reported, payment.Provider ?? "none");
        }

        inbox.Add(provider, eventId, payload.Type, payment.Id, outcome, now);
        if (!await SaveOnceAsync(cancellationToken).ConfigureAwait(false))
        {
            return Record(provider, new WebhookResult(eventId, WebhookOutcome.Duplicate, payment.Id, null, "Processed concurrently by another delivery."));
        }

        return Record(provider, new WebhookResult(eventId, outcome, payment.Id, payment.Status, update.Detail));
    }

    private static PaymentStatus? ToStatus(string type) => type switch
    {
        "payment.authorized" => PaymentStatus.Authorized,
        "payment.captured" => PaymentStatus.Captured,
        "payment.failed" => PaymentStatus.Failed,
        "payment.expired" => PaymentStatus.Expired,
        _ => null,
    };

    /// <summary>Saves; returns false if the same event id was committed by a concurrent delivery.</summary>
    private async Task<bool> SaveOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await payments.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DuplicateKeyException)
        {
            return false;
        }
    }

    private WebhookResult Record(string provider, WebhookResult result)
    {
        metrics.Webhook(provider, result.Outcome.ToString().ToLowerInvariant());
        LogHandled(logger, provider, result.EventId, result.Outcome, result.PaymentId);
        return result;
    }

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Webhook {WebhookEventId} from {Provider}: {Outcome} (payment {PaymentId})")]
    private static partial void LogHandled(ILogger logger, string provider, string webhookEventId, WebhookOutcome outcome, Guid? paymentId);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Error, Message = "Possible double charge: {Provider} reports {Status} for payment {PaymentId}, which {Owner} already owns. Reverse one of the charges")]
    private static partial void LogPossibleDoubleCharge(ILogger logger, Guid paymentId, string provider, PaymentStatus status, string owner);
}
