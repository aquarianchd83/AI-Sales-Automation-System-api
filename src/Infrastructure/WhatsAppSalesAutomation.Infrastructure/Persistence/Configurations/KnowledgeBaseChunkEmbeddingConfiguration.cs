using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeBaseChunkEmbeddingConfiguration : IEntityTypeConfiguration<KnowledgeBaseChunkEmbedding>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseChunkEmbedding> builder)
    {
        builder.ToTable("KnowledgeBaseChunkEmbeddings");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Provider).IsRequired().HasMaxLength(20);
        builder.Property(e => e.Model).IsRequired().HasMaxLength(100);
        // See KnowledgeBaseChunk.Embedding's doc comment for why this is JSON text, not varbinary/vector.
        builder.Property(e => e.Embedding).IsRequired().HasColumnType("nvarchar(max)");

        // One row per (chunk, provider) - ReembedAsync upserts against this the same way
        // KnowledgeBaseArticleModelPublicationConfiguration's own unique index lets PublishToModelAsync
        // upsert rather than relying on application-level de-duplication.
        builder.HasIndex(e => new { e.ChunkId, e.Provider }).IsUnique();

        // No back-navigation on KnowledgeBaseChunk needed - same "configure the FK from the child
        // side, no nav property required" pattern EF Core allows, mirroring
        // KnowledgeBaseArticleConfiguration's Chunks/ModelPublications cascade for consistency if a
        // chunk (or, transitively, its article) is ever hard-deleted.
        builder.HasOne<KnowledgeBaseChunk>()
            .WithMany()
            .HasForeignKey(e => e.ChunkId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
