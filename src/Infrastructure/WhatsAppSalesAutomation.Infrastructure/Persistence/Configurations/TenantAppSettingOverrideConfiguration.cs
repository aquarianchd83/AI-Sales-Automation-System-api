using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class TenantAppSettingOverrideConfiguration : IEntityTypeConfiguration<TenantAppSettingOverride>
{
    public void Configure(EntityTypeBuilder<TenantAppSettingOverride> builder)
    {
        builder.ToTable("TenantAppSettingOverrides");
        builder.HasKey(o => new { o.TenantId, o.Key });

        builder.Property(o => o.Key).IsRequired().HasMaxLength(100);
        builder.Property(o => o.Value).IsRequired().HasMaxLength(2000);

        // No HasQueryFilter here - TenantAppSettingOverride is ITenantOwned, so the combined filter is
        // built reflectively in ApplicationDbContext.OnModelCreating, same as every other ITenantOwned
        // entity (see TenantWhatsAppConfigConfiguration's equivalent comment).
    }
}
