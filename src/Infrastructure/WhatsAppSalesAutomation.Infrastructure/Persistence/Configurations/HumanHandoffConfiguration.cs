using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class HumanHandoffConfiguration : IEntityTypeConfiguration<HumanHandoff>
{
    public void Configure(EntityTypeBuilder<HumanHandoff> builder)
    {
        builder.ToTable("HumanHandoffs");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.TriggerReason).HasConversion<string>().HasMaxLength(20);
        builder.Property(h => h.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(h => h.Notes).HasMaxLength(2000);

        builder.HasIndex(h => h.Status);

        // Cascade: a handoff is an owned child of its conversation, not an independent record -
        // matches Campaign -> CampaignSteps.
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(h => h.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
