using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        builder.ToTable("Leads");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Stage).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.Score).HasConversion<string>().HasMaxLength(10);
        builder.Property(l => l.Budget).HasMaxLength(200);
        builder.Property(l => l.Interest).HasMaxLength(500);
        builder.Property(l => l.PurchaseTimeline).HasMaxLength(200);
        builder.Property(l => l.RowVersion).IsRowVersion();

        // Not unique: history is kept across Won/Lost, matching Conversation's rule - only Application
        // logic enforces "at most one non-terminal Lead per customer" when creating new ones.
        builder.HasIndex(l => l.CustomerId);
        builder.HasIndex(l => l.Stage);

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(l => l.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(l => l.Activities)
            .WithOne()
            .HasForeignKey(a => a.LeadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
