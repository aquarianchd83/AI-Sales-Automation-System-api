using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Mode).HasConversion<string>().HasMaxLength(10);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(10);
        builder.Property(c => c.LastDetectedIntent).HasMaxLength(100);
        builder.Property(c => c.LastLeadScore).HasConversion<string>().HasMaxLength(10);
        builder.Property(c => c.Summary).HasColumnType("nvarchar(max)");

        // Not unique: history is kept (a customer can have several Closed conversations over time),
        // just indexed for the "does this customer already have an active thread" lookup.
        builder.HasIndex(c => new { c.CustomerId, c.Status });

        // Restrict, matching Customer's other references (CampaignCustomer, Message): customers are
        // soft-deleted, never hard-deleted, so this should never actually need to cascade or null out.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(c => c.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
