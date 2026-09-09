using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.FullName).IsRequired().HasMaxLength(200);
        builder.HasIndex(u => u.TenantId);

        // No HasQueryFilter here - ApplicationUser is deliberately not ITenantOwned (its TenantId is
        // nullable, to represent a PlatformSuperAdmin), so ApplicationDbContext.OnModelCreating wires
        // its filter as an explicit special case alongside the reflective ITenantOwned pass.
    }
}
