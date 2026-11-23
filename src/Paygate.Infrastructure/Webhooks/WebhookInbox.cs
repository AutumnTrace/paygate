using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paygate.Application.Webhooks;
using Paygate.Infrastructure.Persistence;

namespace Paygate.Infrastructure.Webhooks;

/// <summary>A webhook event the gateway has processed. The primary key makes processing exactly-once.</summary>
public sealed class WebhookEvent
{
    public required string Provider { get; init; }

    public required string EventId { get; init; }

    public required string Type { get; init; }

    public Guid? PaymentId { get; init; }

    public WebhookOutcome Outcome { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }
}

internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events");
        builder.HasKey(e => new { e.Provider, e.EventId });
        builder.Property(e => e.Provider).HasMaxLength(32);
        builder.Property(e => e.EventId).HasMaxLength(128);
        builder.Property(e => e.Type).HasMaxLength(64);
        builder.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(e => e.PaymentId);
    }
}

/// <summary>Uses the request's DbContext, so the inbox row commits in the same transaction as the payment.</summary>
internal sealed class EfWebhookInbox(PaygateDbContext db) : IWebhookInbox
{
    public Task<bool> ContainsAsync(string provider, string eventId, CancellationToken cancellationToken) =>
        db.Set<WebhookEvent>().AnyAsync(e => e.Provider == provider && e.EventId == eventId, cancellationToken);

    public void Add(string provider, string eventId, string type, Guid? paymentId, WebhookOutcome outcome, DateTimeOffset receivedAt) =>
        db.Set<WebhookEvent>().Add(new WebhookEvent
        {
            Provider = provider,
            EventId = eventId,
            Type = type.Length > 64 ? type[..64] : type,
            PaymentId = paymentId,
            Outcome = outcome,
            ReceivedAt = receivedAt,
        });
}
