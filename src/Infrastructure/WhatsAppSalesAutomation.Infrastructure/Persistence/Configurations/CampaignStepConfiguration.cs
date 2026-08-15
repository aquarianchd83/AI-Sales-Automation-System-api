using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class CampaignStepConfiguration : IEntityTypeConfiguration<CampaignStep>
{
    public void Configure(EntityTypeBuilder<CampaignStep> builder)
    {
        builder.ToTable("CampaignSteps");
        builder.HasKey(s => s.Id);

        // StepType is a plain string (see CampaignStepTypeName) - "FollowUp" plus a number has no
        // fixed upper length now that follow-ups are unbounded, so this is generous headroom
        // rather than a real constraint.
        builder.Property(s => s.StepType).HasMaxLength(50);
        builder.Property(s => s.MessageText).IsRequired().HasMaxLength(2000);

        // At most one row per step position per campaign - enforces "1 Initial + up to 4 follow-ups".
        builder.HasIndex(s => new { s.CampaignId, s.StepNumber }).IsUnique();

        builder.HasMany(s => s.StepMedia)
            .WithOne()
            .HasForeignKey(m => m.CampaignStepId)
            .OnDelete(DeleteBehavior.Cascade);

        // No navigation to MessageTemplate by design (matches Customer.AssignedAgentId's style for
        // cross-aggregate references) - Restrict so retiring a template cannot silently orphan steps
        // that still reference it; the Application layer must repoint or deactivate the step first.
        builder.HasOne<Domain.Entities.Messaging.MessageTemplate>()
            .WithMany()
            .HasForeignKey(s => s.MessageTemplateId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
