using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Slug).IsRequired().HasMaxLength(63);
        builder.HasIndex(t => t.Slug).IsUnique();

        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.CountryCode).HasMaxLength(2);
        builder.Property(t => t.Timezone).HasMaxLength(50);

        // No FK constraint to ApplicationUser/Plan by design - same "loose Guid" treatment every other
        // cross-aggregate reference in this codebase already uses (AssignedAgentId, CreatedBy, etc.).
    }
}
