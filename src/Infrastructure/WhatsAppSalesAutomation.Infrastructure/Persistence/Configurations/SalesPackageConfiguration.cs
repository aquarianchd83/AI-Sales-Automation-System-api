using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Packages;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class SalesPackageConfiguration : IEntityTypeConfiguration<SalesPackage>
{
    public void Configure(EntityTypeBuilder<SalesPackage> builder)
    {
        builder.ToTable("SalesPackages");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(150);
        builder.Property(p => p.Description).HasMaxLength(1000);
        builder.Property(p => p.Price).HasColumnType("decimal(18,2)");
        builder.Property(p => p.DurationUnit).HasConversion<string>().HasMaxLength(10);
        builder.Property(p => p.FeaturesText).HasMaxLength(4000);

        // Unique per tenant, not globally: two tenants may both sell a "Gold" package.
        builder.HasIndex(p => new { p.TenantId, p.Name }).IsUnique();
    }
}
