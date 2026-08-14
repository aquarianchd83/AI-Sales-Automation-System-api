using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class CampaignCustomerConfiguration : IEntityTypeConfiguration<CampaignCustomer>
{
    public void Configure(EntityTypeBuilder<CampaignCustomer> builder)
    {
        builder.ToTable("CampaignCustomers");
        builder.HasKey(cc => cc.Id);

        builder.Property(cc => cc.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(cc => cc.StoppedReason).HasMaxLength(500);
        builder.Property(cc => cc.RowVersion).IsRowVersion();

        builder.HasIndex(cc => new { cc.CampaignId, cc.CustomerId }).IsUnique();

        // Restrict: customers are soft-deleted, never hard-deleted, so this FK should never actually
        // need to cascade or null out in practice - Restrict makes that assumption fail loudly
        // instead of silently if it is ever violated.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(cc => cc.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
