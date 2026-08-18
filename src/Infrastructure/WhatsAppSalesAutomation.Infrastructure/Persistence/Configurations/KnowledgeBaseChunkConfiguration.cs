using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeBaseChunkConfiguration : IEntityTypeConfiguration<KnowledgeBaseChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseChunk> builder)
    {
        builder.ToTable("KnowledgeBaseChunks");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.ChunkText).IsRequired().HasColumnType("nvarchar(max)");
        // See KnowledgeBaseChunk.Embedding's doc comment for why this is JSON text, not varbinary/vector.
        builder.Property(c => c.Embedding).HasColumnType("nvarchar(max)");

        builder.HasIndex(c => c.ArticleId);
    }
}
