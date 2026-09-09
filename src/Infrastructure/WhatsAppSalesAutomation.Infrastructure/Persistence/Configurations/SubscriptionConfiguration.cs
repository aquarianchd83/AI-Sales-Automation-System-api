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

        builder.Property(s => s.StripeCustomerId).HasMaxLength(100);
        builder.Property(s => s.StripeSubscriptionId).HasMaxLength(100);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);

        // One row per tenant in practice (StripeWebhookHandler/BillingService upsert by TenantId
        // rather than inserting a new row per Stripe event) - enforced here, not left to convention,
        // since a second row would silently make PlanLimitsService's "the tenant's subscription" and
        // AuthService.LoginAsync's status check both ambiguous about which one is authoritative.
        builder.HasIndex(s => s.TenantId).IsUnique();

        // StripeCustomerId/StripeSubscriptionId are the two keys StripeWebhookHandler resolves a
        // tenant by - both cross-tenant lookups need IgnoreQueryFilters() (see that class), so a
        // plain index (not unique on a nullable column already covered by the TenantId one above) is
        // enough; Stripe itself guarantees each id's own uniqueness on its side.
        builder.HasIndex(s => s.StripeCustomerId);
        builder.HasIndex(s => s.StripeSubscriptionId);

        // No HasQueryFilter here - Subscription is ITenantOwned, so the combined filter is built
        // reflectively in ApplicationDbContext.OnModelCreating, same as every other ITenantOwned
        // entity.
    }
}
