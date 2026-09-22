using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AutoCampaignEnrollmentConfiguration : IEntityTypeConfiguration<AutoCampaignEnrollment>
{
    public void Configure(EntityTypeBuilder<AutoCampaignEnrollment> builder)
    {
        builder.ToTable("AutoCampaignEnrollments");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Reason).HasMaxLength(1000);

        // "Today's execution campaign for this source campaign" lookup - see the entity's own doc
        // comment. Not unique: a Skipped/Failed row for the same day/source is expected to coexist
        // with a later Started one for a different customer.
        builder.HasIndex(e => new { e.TenantId, e.SourceCampaignId, e.ExecutionDateLocal });

        // The idempotency guard: once a customer has a Started row for a source campaign, a repeated
        // lead-discovery run must never enroll them into it again. Filtered rather than a plain unique
        // index, because Skipped/Failed rows are expected to repeat (e.g. the same disabled
        // configuration skipping every customer it discovers).
        builder.HasIndex(e => new { e.TenantId, e.SourceCampaignId, e.CustomerId })
            .IsUnique()
            .HasFilter("[Status] = 'Started'");
    }
}
