using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Paygate.Application.Events;

namespace Paygate.Infrastructure.Events;

/// <summary>
/// The local stand-in for Kafka: logs each event and keeps the most recent ones in memory so they
/// can be inspected through <c>GET /v1/admin/events</c>.
/// </summary>
public sealed partial class InMemoryEventPublisher(ILogger<InMemoryEventPublisher> logger) : IEventPublisher
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<EventEnvelope> _recent = new();

    public Task PublishAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var envelope in events)
        {
            _recent.Enqueue(envelope);
            LogPublished(logger, envelope.Type, envelope.Key);
        }

        while (_recent.Count > Capacity && _recent.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<EventEnvelope> Recent(int limit) =>
        _recent.Reverse().Take(Math.Clamp(limit, 1, Capacity)).ToList();

    [LoggerMessage(EventId = 7101, Level = LogLevel.Information, Message = "Event {EventType} for {Key}")]
    private static partial void LogPublished(ILogger logger, string eventType, string key);
}
