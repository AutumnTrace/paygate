namespace Paygate.Infrastructure.Events;

public enum EventPublisherKind
{
    /// <summary>Keeps recent events in memory and logs them. Nothing to install; the default for <c>dotnet run</c>.</summary>
    InMemory,

    /// <summary>Publishes to a Kafka topic (docker compose, production).</summary>
    Kafka,
}

public sealed class EventsOptions
{
    public const string SectionName = "Events";

    public EventPublisherKind Publisher { get; set; } = EventPublisherKind.InMemory;

    /// <summary>How often the dispatcher looks for unpublished events.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; set; } = 100;

    /// <summary>Published events are kept this long for troubleshooting, then deleted.</summary>
    public TimeSpan PublishedRetention { get; set; } = TimeSpan.FromDays(1);
}

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string Topic { get; set; } = "payments.v1";

    public string ClientId { get; set; } = "paygate";

    public TimeSpan MessageTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
