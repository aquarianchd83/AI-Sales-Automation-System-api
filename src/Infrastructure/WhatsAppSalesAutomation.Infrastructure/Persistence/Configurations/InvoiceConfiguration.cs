using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.PlanName).HasMaxLength(100);
        builder.Property(i => i.CurrencyCode).IsRequired().HasMaxLength(3);
        builder.Property(i => i.CurrencySymbol).IsRequired().HasMaxLength(10);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(10);

        foreach (var amount in new[]
                 {
                     nameof(Invoice.SubscriptionAmountLocal), nameof(Invoice.LeadDiscoveryAmountLocal),
                     nameof(Invoice.WhatsAppAmountLocal), nameof(Invoice.AiConversationAmountLocal),
                     nameof(Invoice.TotalAmountLocal)
                 })
        {
            builder.Property(amount).HasColumnType("decimal(18,6)");
        }

        foreach (var amount in new[]
                 {
                     nameof(Invoice.SubscriptionAmountUsd), nameof(Invoice.LeadDiscoveryAmountUsd),
                     nameof(Invoice.WhatsAppAmountUsd), nameof(Invoice.AiConversationAmountUsd),
                     nameof(Invoice.TotalAmountUsd)
                 })
        {
            builder.Property(amount).HasColumnType("decimal(18,6)");
        }

        // One invoice per tenant per billed month - what makes the generation pass idempotent (it
        // upserts by this pair rather than blindly inserting on every pass).
        builder.HasIndex(i => new { i.TenantId, i.PeriodStartUtc }).IsUnique();

        // No HasQueryFilter here - Invoice is ITenantOwned, so the combined filter is built
        // reflectively in ApplicationDbContext.OnModelCreating, same as every other ITenantOwned
        // entity (see SubscriptionConfiguration's identical comment).
    }
}
