using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("Plans");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Code).IsRequired().HasMaxLength(50);
        builder.HasIndex(p => p.Code).IsUnique();

        builder.Property(p => p.Name).IsRequired().HasMaxLength(100);
        builder.Property(p => p.StripePriceId).HasMaxLength(100);

        // No HasQueryFilter here - Plan is the platform-global catalog, not ITenantOwned, by design.
    }
}
