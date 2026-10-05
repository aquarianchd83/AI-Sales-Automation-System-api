using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class LeadFollowUpConfiguration : IEntityTypeConfiguration<LeadFollowUp>
{
    public void Configure(EntityTypeBuilder<LeadFollowUp> builder)
    {
        builder.ToTable("LeadFollowUps");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(f => f.Reason).HasMaxLength(500);
        builder.Property(f => f.OutcomeNote).HasMaxLength(500);

        builder.HasIndex(f => f.LeadId);

        // What the sender job and the follow-up list both read: the Scheduled rows, soonest first.
        builder.HasIndex(f => new { f.TenantId, f.Status, f.DueAt });

        // At most one pending follow-up per lead, enforced here as well as in LeadFollowUpService - two
        // agents scheduling at the same moment must not leave a customer with two reminders queued.
        builder.HasIndex(f => f.LeadId)
            .IsUnique()
            .HasFilter("[Status] = 'Scheduled'")
            .HasDatabaseName("IX_LeadFollowUps_LeadId_Scheduled");

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(f => f.LeadId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(f => f.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
