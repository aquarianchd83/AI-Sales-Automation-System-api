using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class RazorpayOrderConfiguration : IEntityTypeConfiguration<RazorpayOrder>
{
    public void Configure(EntityTypeBuilder<RazorpayOrder> builder)
    {
        builder.ToTable("RazorpayOrders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.OrderId).IsRequired().HasMaxLength(40);
        builder.Property(o => o.Receipt).IsRequired().HasMaxLength(40);
        builder.Property(o => o.Currency).IsRequired().HasMaxLength(3);
        builder.Property(o => o.Description).IsRequired().HasMaxLength(255);
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.Mode).IsRequired().HasMaxLength(4);
        builder.Property(o => o.PaymentId).HasMaxLength(40);
        builder.Property(o => o.Method).HasMaxLength(30);
        builder.Property(o => o.LastRefundId).HasMaxLength(40);
        builder.Property(o => o.FailureReason).HasMaxLength(500);
        builder.Property(o => o.LastWebhookEvent).HasMaxLength(60);

        builder.HasIndex(o => o.OrderId).IsUnique();
        builder.HasIndex(o => o.PaymentId);
        builder.HasIndex(o => o.CreatedAt);
    }
}

public class RazorpayWebhookEventConfiguration : IEntityTypeConfiguration<RazorpayWebhookEvent>
{
    public void Configure(EntityTypeBuilder<RazorpayWebhookEvent> builder)
    {
        builder.ToTable("RazorpayWebhookEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventId).IsRequired().HasMaxLength(100);
        builder.Property(e => e.Event).IsRequired().HasMaxLength(60);
        builder.Property(e => e.OrderId).HasMaxLength(40);
        builder.Property(e => e.PaymentId).HasMaxLength(40);
        builder.Property(e => e.SubscriptionId).HasMaxLength(40);
        builder.Property(e => e.Note).HasMaxLength(500);
        // The whole body, uncapped: an event that failed to apply is applied again from this column.
        builder.Property(e => e.Payload).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(e => e.ProcessingStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.ErrorMessage).HasMaxLength(1000);

        // A redelivery of the same event is recognised by this, even when two arrive at once.
        builder.HasIndex(e => e.EventId).IsUnique();
        builder.HasIndex(e => e.CreatedAt);
        // What the processing job and the retry sweep read: everything not yet finished, oldest first.
        builder.HasIndex(e => new { e.ProcessingStatus, e.CreatedAt });
        builder.HasIndex(e => e.SubscriptionId);
    }
}

public class RazorpayPlanConfiguration : IEntityTypeConfiguration<RazorpayPlan>
{
    public void Configure(EntityTypeBuilder<RazorpayPlan> builder)
    {
        builder.ToTable("RazorpayPlans");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Mode).IsRequired().HasMaxLength(4);
        builder.Property(p => p.Currency).IsRequired().HasMaxLength(3);
        builder.Property(p => p.Period).IsRequired().HasMaxLength(10);
        builder.Property(p => p.RazorpayPlanId).IsRequired().HasMaxLength(40);

        builder.HasOne<Plan>().WithMany().HasForeignKey(p => p.PlanId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.RazorpayPlanId).IsUnique();
        // One Razorpay plan per distinct charge: two checkouts for the same plan and price can never create two.
        builder.HasIndex(p => new { p.PlanId, p.Mode, p.Currency, p.AmountMinor, p.Period, p.Interval }).IsUnique();
    }
}

public class RazorpaySubscriptionConfiguration : IEntityTypeConfiguration<RazorpaySubscription>
{
    public void Configure(EntityTypeBuilder<RazorpaySubscription> builder)
    {
        builder.ToTable("RazorpaySubscriptions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.RazorpaySubscriptionId).IsRequired().HasMaxLength(40);
        builder.Property(s => s.Mode).IsRequired().HasMaxLength(4);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Currency).IsRequired().HasMaxLength(3);
        builder.Property(s => s.ShortUrl).HasMaxLength(300);
        builder.Property(s => s.PaymentMethod).HasMaxLength(30);
        builder.Property(s => s.CustomerId).HasMaxLength(40);
        builder.Property(s => s.LastEvent).HasMaxLength(60);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Plan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RazorpayPlan>().WithMany().HasForeignKey(s => s.RazorpayPlanRowId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.RazorpaySubscriptionId).IsUnique();
        builder.HasIndex(s => new { s.TenantId, s.Status });
        // At most one authorisation waiting per tenant: two checkouts started at the same moment cannot both create a mandate to approve.
        builder.HasIndex(s => s.TenantId).IsUnique().HasFilter("[Status] = 'Created'").HasDatabaseName("IX_RazorpaySubscriptions_TenantId_PendingAuthorization");
        // What reconciliation walks: everything still alive, least recently checked first.
        builder.HasIndex(s => new { s.Status, s.LastReconciledAtUtc });

        // No HasQueryFilter here - RazorpaySubscription is ITenantOwned, so the combined filter is built reflectively in
        // ApplicationDbContext.OnModelCreating, same as every other ITenantOwned entity.
    }
}

/// <summary>A tenant's wallet top-up through Razorpay. The unique indexes are what make crediting exactly-once at the database level: one row per
/// Razorpay order, and a gateway payment id that can belong to only one top-up.</summary>
public class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.ToTable("PaymentTransactions");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.InternalTransactionId).IsRequired().HasMaxLength(40);
        builder.Property(t => t.RazorpayOrderId).IsRequired().HasMaxLength(40);
        builder.Property(t => t.RazorpayPaymentId).HasMaxLength(40);
        builder.Property(t => t.Amount).HasColumnType("decimal(18,2)");
        builder.Property(t => t.TaxAmount).HasColumnType("decimal(18,2)");
        builder.Property(t => t.TotalAmount).HasColumnType("decimal(18,2)");
        builder.Property(t => t.Credits).HasColumnType("decimal(18,2)");
        builder.Property(t => t.CreditsPerRupee).HasColumnType("decimal(18,6)");
        builder.Property(t => t.FxRateToInr).HasColumnType("decimal(18,6)");
        builder.Property(t => t.TotalInr).HasColumnType("decimal(18,2)");
        builder.Property(t => t.Currency).IsRequired().HasMaxLength(3);
        builder.Property(t => t.CountryCode).HasMaxLength(2);
        builder.Property(t => t.StateCode).HasMaxLength(5);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.PaymentMethod).HasMaxLength(30);
        builder.Property(t => t.Mode).IsRequired().HasMaxLength(4);
        builder.Property(t => t.FailureReason).HasMaxLength(500);

        builder.HasIndex(t => t.InternalTransactionId).IsUnique();
        builder.HasIndex(t => t.RazorpayOrderId).IsUnique();
        builder.HasIndex(t => t.RazorpayPaymentId).IsUnique().HasFilter("[RazorpayPaymentId] IS NOT NULL");
        builder.HasIndex(t => new { t.TenantId, t.CreatedAt });
        // What the reconciliation job reads: everything still waiting, oldest first.
        builder.HasIndex(t => new { t.Status, t.CreatedAt });

        // No HasQueryFilter here - PaymentTransaction is ITenantOwned, so the combined filter is built reflectively in ApplicationDbContext.OnModelCreating.
    }
}
