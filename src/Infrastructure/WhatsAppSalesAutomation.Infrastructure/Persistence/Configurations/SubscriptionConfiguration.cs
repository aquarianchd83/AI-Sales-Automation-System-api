using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("Subscriptions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);

        // One row per tenant in practice (BillingService.ChoosePlanAsync upserts by TenantId rather
        // than inserting a new row per purchase) - enforced here, not left to convention, since a
        // second row would silently make PlanLimitsService's "the tenant's subscription" and
        // AuthService.LoginAsync's status check both ambiguous about which one is authoritative.
        builder.HasIndex(s => s.TenantId).IsUnique();

        // No HasQueryFilter here - Subscription is ITenantOwned, so the combined filter is built
        // reflectively in ApplicationDbContext.OnModelCreating, same as every other ITenantOwned
        // entity.
    }
}
