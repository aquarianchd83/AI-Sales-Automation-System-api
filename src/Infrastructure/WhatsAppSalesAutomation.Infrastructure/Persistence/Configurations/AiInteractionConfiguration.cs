using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AiInteractionConfiguration : IEntityTypeConfiguration<AiInteraction>
{
    public void Configure(EntityTypeBuilder<AiInteraction> builder)
    {
        builder.ToTable("AiInteractions");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.DetectedIntent).HasMaxLength(100);
        builder.Property(a => a.ExtractedEntitiesJson).HasColumnType("nvarchar(max)");
        builder.Property(a => a.ProposedResponseText).HasColumnType("nvarchar(max)");
        builder.Property(a => a.ActionTaken).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.ModelUsed).IsRequired().HasMaxLength(100);

        builder.HasIndex(a => a.ConversationId);

        // Cascade: an AI turn is an owned child of its conversation - matches HumanHandoff.
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(a => a.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not Cascade: Messages are never deleted in practice (the append-only log), but if
        // one ever were, silently cascading away the AI's own record of processing it would erase
        // audit trail that should instead fail loudly.
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(a => a.InboundMessageId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
