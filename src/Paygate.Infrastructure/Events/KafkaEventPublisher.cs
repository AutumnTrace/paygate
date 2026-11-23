using System.Diagnostics;
using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paygate.Application.Events;
using Paygate.Application.Observability;

namespace Paygate.Infrastructure.Events;

/// <summary>
/// Publishes events to Kafka using the CloudEvents Kafka binding (binary mode): the JSON payload is
/// the message value, metadata travels in <c>ce_*</c> headers, and the payment id is the message key
/// so every event of one payment lands in the same partition, in order.
/// The producer is idempotent with <c>acks=all</c>, so broker retries cannot duplicate or reorder messages.
/// </summary>
internal sealed partial class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaOptions _options;

    public KafkaEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        var config = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            ClientId = _options.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = 5,
            CompressionType = CompressionType.Lz4,
            MessageTimeoutMs = (int)_options.MessageTimeout.TotalMilliseconds,
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => LogKafkaError(logger, error.Code, error.Reason))
            .Build();
    }

    public async Task PublishAsync(IReadOnlyList<EventEnvelope> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        using var activity = PaygateTelemetry.ActivitySource.StartActivity($"{_options.Topic} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", _options.Topic);
        activity?.SetTag("messaging.batch.message_count", events.Count);

        // Produce in order without awaiting each one: librdkafka keeps per-partition order for an
        // idempotent producer, and the batch is acknowledged as a whole.
        var deliveries = events.Select(e => _producer.ProduceAsync(_options.Topic, ToMessage(e), cancellationToken)).ToList();
        await Task.WhenAll(deliveries).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }

    private static Message<string, string> ToMessage(EventEnvelope envelope)
    {
        var headers = new Headers
        {
            { "ce_specversion", Encoding.UTF8.GetBytes("1.0") },
            { "ce_id", Encoding.UTF8.GetBytes(envelope.Id.ToString()) },
            { "ce_type", Encoding.UTF8.GetBytes(envelope.Type) },
            { "ce_source", Encoding.UTF8.GetBytes("/paygate") },
            { "ce_time", Encoding.UTF8.GetBytes(envelope.OccurredAt.ToString("O", CultureInfo.InvariantCulture)) },
            { "content-type", Encoding.UTF8.GetBytes("application/json") },
        };

        if (envelope.TraceParent is not null)
        {
            headers.Add("traceparent", Encoding.UTF8.GetBytes(envelope.TraceParent));
        }

        return new Message<string, string>
        {
            Key = envelope.Key,
            Value = envelope.Payload,
            Timestamp = new Timestamp(envelope.OccurredAt),
            Headers = headers,
        };
    }

    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning, Message = "Kafka client error {Code}: {Reason}")]
    private static partial void LogKafkaError(ILogger logger, ErrorCode code, string reason);
}

/// <summary>Readiness: the broker answers a metadata request for the events topic.</summary>
internal sealed class KafkaHealthCheck(IOptions<KafkaOptions> options) : IHealthCheck, IDisposable
{
    private readonly IAdminClient _admin = new AdminClientBuilder(new AdminClientConfig
    {
        BootstrapServers = options.Value.BootstrapServers,
        ClientId = options.Value.ClientId + "-health",
    }).Build();

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                try
                {
                    var metadata = _admin.GetMetadata(options.Value.Topic, TimeSpan.FromSeconds(3));
                    return metadata.Brokers.Count > 0
                        ? HealthCheckResult.Healthy($"{metadata.Brokers.Count} broker(s)")
                        : HealthCheckResult.Unhealthy("No Kafka brokers available.");
                }
                catch (KafkaException ex)
                {
                    return HealthCheckResult.Unhealthy("Kafka is unreachable.", ex);
                }
            },
            cancellationToken);

    public void Dispose() => _admin.Dispose();
}
