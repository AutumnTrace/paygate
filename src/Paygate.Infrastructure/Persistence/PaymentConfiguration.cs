using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.Infrastructure.Persistence;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    private static readonly ValueConverter<Currency, string> CurrencyConverter = new(c => c.Code, code => Currency.FromCode(code));

    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.MerchantId).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Method).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(p => p.Country).HasMaxLength(2).IsFixedLength();
        builder.Property(p => p.CustomerId).HasMaxLength(Payment.MaxCustomerIdLength);
        builder.Property(p => p.Provider).HasMaxLength(32);
        builder.Property(p => p.ProviderReference).HasMaxLength(64);
        builder.Property(p => p.FailureCode).HasMaxLength(64);
        builder.Property(p => p.FailureMessage).HasMaxLength(512);

        // Money is stored as integer minor units plus an ISO-4217 code, never as a float.
        builder.ComplexProperty(p => p.Amount, money =>
        {
            money.Property(m => m.MinorUnits).HasColumnName("amount_minor");
            money.Property(m => m.Currency).HasConversion(CurrencyConverter).HasColumnName("currency").HasMaxLength(3);
        });
        builder.ComplexProperty(p => p.RefundedAmount, money =>
        {
            money.Property(m => m.MinorUnits).HasColumnName("refunded_minor");
            money.Property(m => m.Currency).HasConversion(CurrencyConverter).HasColumnName("refunded_currency").HasMaxLength(3);
        });

        // Optimistic concurrency: the aggregate bumps Version on every change, and EF adds
        // "WHERE version = @original" to the UPDATE. A concurrent webhook and refund cannot both win.
        builder.Property(p => p.Version).IsConcurrencyToken();

        builder.Ignore(p => p.DomainEvents);
        builder.Ignore(p => p.RefundableAmount);

        builder.HasMany(p => p.Attempts).WithOne().HasForeignKey("PaymentId").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Attempts).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(p => p.Refunds).WithOne().HasForeignKey("PaymentId").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Refunds).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);

        // Listing a merchant's payments newest-first is an index range scan, not a sort.
        builder.HasIndex(p => new { p.MerchantId, p.CreatedAt }).IsDescending(false, true);

        // Webhooks find payments by the provider's reference.
        builder.HasIndex(p => new { p.Provider, p.ProviderReference }).IsUnique();
    }
}

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Provider).HasMaxLength(32);
        builder.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(24);
        builder.Property(a => a.Error).HasMaxLength(256);
        builder.HasIndex("PaymentId", nameof(PaymentAttempt.Sequence)).IsUnique();
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    private static readonly ValueConverter<Currency, string> CurrencyConverter = new(c => c.Code, code => Currency.FromCode(code));

    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("refunds");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Reason).HasMaxLength(256);
        builder.Property(r => r.ProviderReference).HasMaxLength(64);
        builder.ComplexProperty(r => r.Amount, money =>
        {
            money.Property(m => m.MinorUnits).HasColumnName("amount_minor");
            money.Property(m => m.Currency).HasConversion(CurrencyConverter).HasColumnName("currency").HasMaxLength(3);
        });
    }
}
