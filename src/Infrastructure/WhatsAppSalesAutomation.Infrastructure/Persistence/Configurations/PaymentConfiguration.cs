using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.PlanName).IsRequired().HasMaxLength(100);
        builder.Property(p => p.CurrencyCode).IsRequired().HasMaxLength(3);
        builder.Property(p => p.CurrencySymbol).IsRequired().HasMaxLength(10);
        builder.Property(p => p.Provider).IsRequired().HasMaxLength(20);
        builder.Property(p => p.LocalAmount).HasColumnType("decimal(18,2)");

        // Not unique, unlike Subscription's TenantId index - a tenant accumulates many payments
        // over time, this is just the lookup PlanLimitsService-style code would use to page/filter
        // by tenant.
        builder.HasIndex(p => p.TenantId);

        // No HasQueryFilter here - Payment is ITenantOwned, so the combined filter is built
        // reflectively in ApplicationDbContext.OnModelCreating, same as every other ITenantOwned
        // entity (see SubscriptionConfiguration's identical comment).
    }
}
