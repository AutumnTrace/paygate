namespace Paygate.Application.Events;

/// <summary>
/// A domain event ready to leave the service: a stable type name, a partition key (the payment id,
/// so all events of one payment stay in order) and a JSON payload.
/// </summary>
/// <param name="Id">Unique event id. Consumers deduplicate on it.</param>
/// <param name="Type">Versioned event type, e.g. <c>payment.status_changed.v1</c>.</param>
/// <param name="Key">Partition key: the payment id.</param>
/// <param name="Payload">The event as JSON.</param>
/// <param name="OccurredAt">When the change happened in the domain.</param>
/// <param name="TraceParent">W3C trace context of the request that caused it, so consumers can join the trace.</param>
public sealed record EventEnvelope(Guid Id, string Type, string Key, string Payload, DateTimeOffset OccurredAt, string? TraceParent);

/// <summary>
/// Publishes domain events to the outside world. Called by the outbox dispatcher, never directly
/// from a request, so a broker outage delays events instead of failing payments.
/// Implementations must be safe to call again with the same events (at-least-once delivery).
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken);
}
