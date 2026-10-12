using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class StudioActivityConfiguration : IEntityTypeConfiguration<StudioActivity>
{
    public void Configure(EntityTypeBuilder<StudioActivity> builder)
    {
        builder.ToTable("StudioActivities");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(a => a.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(a => a.Description).IsRequired().HasMaxLength(300);
        builder.Property(a => a.OperationKey).IsRequired().HasMaxLength(150);
        builder.Property(a => a.ReferenceType).HasMaxLength(50);
        builder.Property(a => a.ReferenceId).HasMaxLength(100);
        builder.Property(a => a.Provider).HasMaxLength(50);
        builder.Property(a => a.Detail).HasMaxLength(300);
        builder.Property(a => a.Units).HasColumnType("decimal(18,4)");
        builder.Property(a => a.UnitsRefunded).HasColumnType("decimal(18,4)");
        builder.Property(a => a.BalanceAfter).HasColumnType("decimal(18,4)");

        // The tenant's activity list, newest first.
        builder.HasIndex(a => new { a.TenantId, a.RequestedAtUtc });
        // The retry lookup: one charge per real-world event, ever.
        builder.HasIndex(a => new { a.TenantId, a.OperationKey }).IsUnique();
    }
}

public class TenantStudioSettingsConfiguration : IEntityTypeConfiguration<TenantStudioSettings>
{
    public void Configure(EntityTypeBuilder<TenantStudioSettings> builder)
    {
        builder.ToTable("TenantStudioSettings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ApprovalPolicy).HasConversion<string>().HasMaxLength(30);

        // At most one row per tenant.
        builder.HasIndex(s => s.TenantId).IsUnique();
    }
}
