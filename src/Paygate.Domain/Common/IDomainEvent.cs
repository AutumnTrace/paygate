namespace Paygate.Domain.Common;

/// <summary>Something that happened in the domain and that other services may care about.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    /// <summary>Stable, versioned event name, e.g. <c>payment.status_changed.v1</c>.</summary>
    string EventType { get; }

    DateTimeOffset OccurredAt { get; }
}
