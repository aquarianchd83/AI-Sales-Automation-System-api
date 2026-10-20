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

        // SetNull: the ingestion swap deletes an article's old chunks when it re-indexes, and under the former Restrict that failed as soon as
        // any chunk of the article had ever been cited. The citation record is kept (what was cited, how relevant, and for product knowledge which
        // item and version); only the pointer to the replaced chunk is cleared.
        builder.HasOne<KnowledgeBaseChunk>()
            .WithMany()
            .HasForeignKey(s => s.KnowledgeBaseChunkId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(s => s.KnowledgeItemId);
    }
}
