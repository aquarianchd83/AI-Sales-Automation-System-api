using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AiInteractionSourceConfiguration : IEntityTypeConfiguration<AiInteractionSource>
{
    public void Configure(EntityTypeBuilder<AiInteractionSource> builder)
    {
        builder.ToTable("AiInteractionSources");
        builder.HasKey(s => s.Id);

        builder.HasIndex(s => s.AiInteractionId);

        // Cascade: citations are owned by the interaction they belong to.
        builder.HasOne<AiInteraction>()
            .WithMany()
            .HasForeignKey(s => s.AiInteractionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: a chunk being deleted/reindexed should not silently delete historical citation
        // records of what was cited at the time - it should fail loudly and be handled explicitly by
        // whatever reindex/delete flow triggers it.
        builder.HasOne<KnowledgeBaseChunk>()
            .WithMany()
            .HasForeignKey(s => s.KnowledgeBaseChunkId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
