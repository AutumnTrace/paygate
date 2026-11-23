using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Paygate.Infrastructure.Idempotency;

public enum IdempotencyRecordStatus
{
    InFlight,
    Completed,
}

/// <summary>One Idempotency-Key, scoped to a merchant, with the response to replay.</summary>
public sealed class IdempotencyRecord
{
    public required string MerchantId { get; init; }

    public required string Key { get; init; }

    /// <summary>SHA-256 of method, path and body. A different request under the same key is rejected.</summary>
    public required string Fingerprint { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public IdempotencyRecordStatus Status { get; set; }

    public int? ResponseStatusCode { get; set; }

    public string? ResponseContentType { get; set; }

    public string? ResponseLocation { get; set; }

    public byte[]? ResponseBody { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>While in flight, the key is locked until this time.</summary>
    public DateTimeOffset LockedUntil { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");

        // The composite primary key is the unique index that makes "first writer wins" hold
        // across concurrent requests and gateway instances.
        builder.HasKey(r => new { r.MerchantId, r.Key });
        builder.Property(r => r.MerchantId).HasMaxLength(64);
        builder.Property(r => r.Key).HasMaxLength(255);
        builder.Property(r => r.Fingerprint).HasMaxLength(64).IsFixedLength();
        builder.Property(r => r.Method).HasMaxLength(8);
        builder.Property(r => r.Path).HasMaxLength(256);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.ResponseContentType).HasMaxLength(128);
        builder.Property(r => r.ResponseLocation).HasMaxLength(256);

        // The cleanup job deletes by expiry.
        builder.HasIndex(r => r.ExpiresAt);
    }
}
