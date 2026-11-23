using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paygate.Application.Events;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.Infrastructure.Events;

/// <summary>
/// A domain event waiting to be published. Written in the same transaction as the change that
/// raised it (transactional outbox), so an event is never lost and never published for a change
/// that was rolled back.
/// </summary>
public sealed class OutboxMessage
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Insertion order. Events are published in this order.</summary>
    public long Id { get; private set; }

    public Guid EventId { get; private init; }

    public required string Type { get; init; }

    public required string Key { get; init; }

    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string? TraceParent { get; init; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public static OutboxMessage From(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return new OutboxMessage
        {
            EventId = domainEvent.EventId,
            Type = domainEvent.EventType,
            Key = domainEvent is PaymentEvent payment ? payment.PaymentId.ToString() : domainEvent.EventId.ToString(),
            Payload = Serialize(domainEvent),
            OccurredAt = domainEvent.OccurredAt,
            TraceParent = Activity.Current?.Id,
        };
    }

    /// <summary>
    /// JSON with the envelope fields first (eventId, eventType, paymentId, merchantId, occurredAt) and the
    /// event-specific fields after them, so every event type reads the same way. Serialization concerns
    /// stay here instead of as attributes on the domain events.
    /// </summary>
    private static string Serialize(IDomainEvent domainEvent)
    {
        var fields = JsonSerializer.SerializeToNode(domainEvent, domainEvent.GetType(), SerializerOptions)!.AsObject();
        var ordered = new JsonObject();
        foreach (var name in LeadingFields)
        {
            if (fields.Remove(name, out var value))
            {
                ordered[name] = value;
            }
        }

        foreach (var (name, value) in fields.ToList())
        {
            fields.Remove(name);
            ordered[name] = value;
        }

        return ordered.ToJsonString(SerializerOptions);
    }

    private static readonly string[] LeadingFields = ["eventId", "eventType", "paymentId", "merchantId", "occurredAt"];

    public EventEnvelope ToEnvelope() => new(EventId, Type, Key, Payload, OccurredAt, TraceParent);
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();
        builder.HasIndex(m => m.EventId).IsUnique();
        builder.Property(m => m.Type).HasMaxLength(64);
        builder.Property(m => m.Key).HasMaxLength(64);
        builder.Property(m => m.TraceParent).HasMaxLength(64);
        builder.Property(m => m.LastError).HasMaxLength(512);

        // The dispatcher's query: oldest unpublished first.
        builder.HasIndex(m => new { m.PublishedAt, m.Id });
    }
}
