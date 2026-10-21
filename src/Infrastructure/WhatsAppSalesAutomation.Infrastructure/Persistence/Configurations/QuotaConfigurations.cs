using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlanQuotaConfiguration : IEntityTypeConfiguration<PlanQuota>
{
    public void Configure(EntityTypeBuilder<PlanQuota> builder)
    {
        builder.ToTable("PlanQuotas");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(p => p.IncludedUnits).HasColumnType("decimal(18,4)");
        builder.HasIndex(p => new { p.PlanId, p.QuotaType }).IsUnique();
    }
}

public class CreditPackConfiguration : IEntityTypeConfiguration<CreditPack>
{
    public void Configure(EntityTypeBuilder<CreditPack> builder)
    {
        builder.ToTable("CreditPacks");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(100);
        builder.Property(p => p.Units).HasColumnType("decimal(18,4)");
    }
}

public class QuotaGrantConfiguration : IEntityTypeConfiguration<QuotaGrant>
{
    public void Configure(EntityTypeBuilder<QuotaGrant> builder)
    {
        builder.ToTable("QuotaGrants");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(g => g.Origin).HasConversion<string>().HasMaxLength(30);
        builder.Property(g => g.UnitsGranted).HasColumnType("decimal(18,4)");
        builder.Property(g => g.UnitsRemaining).HasColumnType("decimal(18,4)");

        // The lookup every spend runs: this tenant's live grants for one quota type, soonest-expiring first.
        builder.HasIndex(g => new { g.TenantId, g.QuotaType, g.ExpiresAtUtc });
    }
}

public class QuotaLedgerEntryConfiguration : IEntityTypeConfiguration<QuotaLedgerEntry>
{
    public void Configure(EntityTypeBuilder<QuotaLedgerEntry> builder)
    {
        builder.ToTable("QuotaLedgerEntries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.EntryType).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.UnitsDelta).HasColumnType("decimal(18,4)");
        builder.Property(e => e.BalanceAfter).HasColumnType("decimal(18,4)");
        builder.Property(e => e.OperationKey).IsRequired().HasMaxLength(200);
        builder.Property(e => e.ReferenceType).HasMaxLength(50);
        builder.Property(e => e.ReferenceId).HasMaxLength(100);
        builder.Property(e => e.Note).HasMaxLength(500);

        builder.HasIndex(e => new { e.TenantId, e.QuotaType, e.OccurredAtUtc });
        // Not unique: one operation can write several rows (a spend across two grants). Idempotency is
        // checked in the service, and the wallet's concurrency token stops two racing retries.
        builder.HasIndex(e => new { e.TenantId, e.OperationKey });
    }
}

public class QuotaWalletConfiguration : IEntityTypeConfiguration<QuotaWallet>
{
    public void Configure(EntityTypeBuilder<QuotaWallet> builder)
    {
        builder.ToTable("QuotaWallets");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(w => w.Balance).HasColumnType("decimal(18,4)");
        builder.Property(w => w.RowVersion).IsRowVersion();
        builder.HasIndex(w => new { w.TenantId, w.QuotaType }).IsUnique();
    }
}

public class RefundRequestConfiguration : IEntityTypeConfiguration<RefundRequest>
{
    public void Configure(EntityTypeBuilder<RefundRequest> builder)
    {
        builder.ToTable("RefundRequests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Reason).IsRequired().HasMaxLength(1000);
        builder.Property(r => r.ReviewNote).HasMaxLength(1000);
        builder.Property(r => r.ProviderReference).HasMaxLength(100);
        builder.Property(r => r.FailureReason).HasMaxLength(500);
        builder.Property(r => r.EligibleLocalAmount).HasColumnType("decimal(18,2)");
        builder.Property(r => r.RefundedLocalAmount).HasColumnType("decimal(18,2)");

        builder.HasIndex(r => new { r.TenantId, r.Status });
        builder.HasIndex(r => r.PaymentId);
    }
}

public class TenantNotificationConfiguration : IEntityTypeConfiguration<TenantNotification>
{
    public void Configure(EntityTypeBuilder<TenantNotification> builder)
    {
        builder.ToTable("TenantNotifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(n => n.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(n => n.EpisodeKey).IsRequired().HasMaxLength(100);
        builder.Property(n => n.Title).IsRequired().HasMaxLength(200);
        builder.Property(n => n.Body).IsRequired().HasMaxLength(1000);
        builder.Property(n => n.EmailStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(n => n.WhatsAppStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(n => n.DeliveryNote).HasMaxLength(500);

        // One notification per (kind, quota type, episode): what makes the alert pass safe to run every
        // few minutes without ever telling the tenant the same thing twice.
        builder.HasIndex(n => new { n.TenantId, n.Kind, n.QuotaType, n.EpisodeKey }).IsUnique();
        builder.HasIndex(n => new { n.TenantId, n.CreatedAt });
    }
}

public class PlanPriceConfiguration : IEntityTypeConfiguration<PlanPrice>
{
    public void Configure(EntityTypeBuilder<PlanPrice> builder)
    {
        builder.ToTable("PlanPrices");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.CountryCode).IsRequired().HasMaxLength(2);
        builder.Property(p => p.Amount).HasColumnType("decimal(18,2)");
        builder.HasIndex(p => new { p.PlanId, p.CountryCode }).IsUnique();
    }
}

public class CreditPackPriceConfiguration : IEntityTypeConfiguration<CreditPackPrice>
{
    public void Configure(EntityTypeBuilder<CreditPackPrice> builder)
    {
        builder.ToTable("CreditPackPrices");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.CountryCode).IsRequired().HasMaxLength(2);
        builder.Property(p => p.Amount).HasColumnType("decimal(18,2)");
        builder.HasIndex(p => new { p.CreditPackId, p.CountryCode }).IsUnique();
    }
}

public class CreditScenarioSettingConfiguration : IEntityTypeConfiguration<CreditScenarioSetting>
{
    public void Configure(EntityTypeBuilder<CreditScenarioSetting> builder)
    {
        builder.ToTable("CreditScenarioSettings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Scenario).HasConversion<string>().HasMaxLength(40);
        builder.Property(s => s.Payer).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.UnitsPerAction).HasColumnType("decimal(18,4)");
        builder.HasIndex(s => s.Scenario).IsUnique();
    }
}

public class PlatformCreditWalletConfiguration : IEntityTypeConfiguration<PlatformCreditWallet>
{
    public void Configure(EntityTypeBuilder<PlatformCreditWallet> builder)
    {
        builder.ToTable("PlatformCreditWallets");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(w => w.Balance).HasColumnType("decimal(18,4)");
        builder.Property(w => w.RowVersion).IsRowVersion();
        builder.HasIndex(w => w.QuotaType).IsUnique();
    }
}

public class PlatformCreditEntryConfiguration : IEntityTypeConfiguration<PlatformCreditEntry>
{
    public void Configure(EntityTypeBuilder<PlatformCreditEntry> builder)
    {
        builder.ToTable("PlatformCreditEntries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.EntryType).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.UnitsDelta).HasColumnType("decimal(18,4)");
        builder.Property(e => e.BalanceAfter).HasColumnType("decimal(18,4)");
        builder.Property(e => e.OperationKey).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Note).HasMaxLength(500);
        builder.HasIndex(e => new { e.QuotaType, e.OccurredAtUtc });
        builder.HasIndex(e => e.OperationKey);
    }
}

public class CreditUsageConfiguration : IEntityTypeConfiguration<CreditUsage>
{
    public void Configure(EntityTypeBuilder<CreditUsage> builder)
    {
        builder.ToTable("CreditUsages");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Scenario).HasConversion<string>().HasMaxLength(40);
        builder.Property(u => u.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(u => u.ConfiguredPayer).HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.ChargedTo).HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.Units).HasColumnType("decimal(18,4)");
        builder.Property(u => u.OperationKey).IsRequired().HasMaxLength(200);
        builder.Property(u => u.ReferenceType).HasMaxLength(50);
        builder.Property(u => u.ReferenceId).HasMaxLength(100);
        builder.Property(u => u.Description).HasMaxLength(300);
        builder.Property(u => u.SettlementNote).HasMaxLength(500);

        builder.HasIndex(u => u.OccurredAtUtc);
        builder.HasIndex(u => new { u.TenantId, u.OccurredAtUtc });
        builder.HasIndex(u => new { u.Scenario, u.OccurredAtUtc });
        // One row per spend: what makes a retried spend a no-op rather than a second record.
        builder.HasIndex(u => new { u.TenantId, u.OperationKey }).IsUnique();
    }
}
