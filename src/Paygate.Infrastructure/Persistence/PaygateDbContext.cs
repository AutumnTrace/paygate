using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;
using Paygate.Infrastructure.Events;
using Paygate.Infrastructure.Idempotency;
using Paygate.Infrastructure.Webhooks;

namespace Paygate.Infrastructure.Persistence;

public sealed class PaygateDbContext(DbContextOptions<PaygateDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Moves the domain events of every changed aggregate into the outbox, then saves both in one
    /// transaction. Nothing is published from here; the outbox dispatcher does that afterwards.
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StageDomainEvents();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StageDomainEvents();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaygateDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite has no native timestamp type and cannot order or compare DateTimeOffset values
        // stored as text, so store them as sortable 64-bit integers there. PostgreSQL keeps timestamptz.
        if (Database.IsSqlite())
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    private void StageDomainEvents()
    {
        var events = ChangeTracker.Entries<AggregateRoot>()
            .SelectMany(entry => entry.Entity.DequeueDomainEvents())
            .ToList();

        foreach (var domainEvent in events)
        {
            OutboxMessages.Add(OutboxMessage.From(domainEvent));
        }
    }
}
