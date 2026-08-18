using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Leads;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class LeadActivityConfiguration : IEntityTypeConfiguration<LeadActivity>
{
    public void Configure(EntityTypeBuilder<LeadActivity> builder)
    {
        builder.ToTable("LeadActivities");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ActivityType).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.OldValue).HasMaxLength(500);
        builder.Property(a => a.NewValue).HasMaxLength(500);
        builder.Property(a => a.Note).HasMaxLength(2000);

        builder.HasIndex(a => a.LeadId);
    }
}
