using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeBaseChunkConfiguration : IEntityTypeConfiguration<KnowledgeBaseChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseChunk> builder)
    {
        builder.ToTable("KnowledgeBaseChunks", table =>
        {
            table.HasCheckConstraint("CK_KBChunks_AuthorityRange", "[AuthorityRank] BETWEEN 0 AND 100");

            // An atomic group is either fully described or not a group. A sequence without a total
            // (or the reverse) would leave the "pull in the whole group" expansion unable to tell
            // whether it had actually pulled in the whole group - the exact failure that rule exists
            // to prevent, arriving silently.
            table.HasCheckConstraint("CK_KBChunks_AtomicGroupComplete",
                "([AtomicGroupId] IS NULL AND [AtomicGroupSequence] IS NULL AND [AtomicGroupTotal] IS NULL) " +
                "OR ([AtomicGroupId] IS NOT NULL AND [AtomicGroupSequence] IS NOT NULL AND [AtomicGroupTotal] IS NOT NULL " +
                "AND [AtomicGroupSequence] BETWEEN 1 AND [AtomicGroupTotal])");
        });

        builder.HasKey(c => c.Id);

        builder.Property(c => c.ContextHeader).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(c => c.ChunkText).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(c => c.EmbeddingInput).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(c => c.SearchText).IsRequired().HasColumnType("nvarchar(max)");

        // See KnowledgeBaseChunk.Embedding's doc comment for why this stays JSON text rather than
        // becoming SQL Server 2025's native VECTOR - in short, it is the portable fallback store and
        // the human-inspectable copy; the native column lives alongside it where the server has one.
        builder.Property(c => c.Embedding).HasColumnType("nvarchar(max)");
        builder.Property(c => c.EmbeddingProvider).HasMaxLength(20);
        builder.Property(c => c.EmbeddingModel).HasMaxLength(100);

        builder.Property(c => c.SourceType).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(c => c.ArticleStatus).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(c => c.ProductModule).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.LanguageCode).IsRequired().HasMaxLength(10);
        builder.Property(c => c.CountryCode).HasMaxLength(2);

        // ── Indexes (§J.3, chunk side) ────────────────────────────────────────────────────

        // The most important index in the retrieval path: it is what narrows the candidate set
        // BEFORE the vector comparison runs. The INCLUDE list carries every column the hard metadata
        // filter and the subsequent boosting need, so that stage is covered and never touches the
        // base table. The filtered predicate keeps inactive chunks - a half-built re-index set - out
        // of the index entirely rather than merely out of the results.
        builder.HasIndex(c => new { c.TenantId, c.ArticleStatus, c.IsActive, c.IsCurrentArticleVersion, c.LanguageCode })
            .HasDatabaseName("IX_KBChunks_Retrieval")
            .IncludeProperties(c => new
            {
                c.ArticleId,
                c.AuthorityRank,
                c.ProductModule,
                c.CountryCode,
                c.EffectiveFrom,
                c.EffectiveTo,
                c.AtomicGroupId,
                c.ChunkIndex
            })
            .HasFilter("[IsActive] = 1");

        builder.HasIndex(c => new { c.ArticleId, c.ChunkIndex })
            .HasDatabaseName("IX_KBChunks_Article");

        // Atomic group expansion reads by group, in sequence order.
        builder.HasIndex(c => new { c.AtomicGroupId, c.AtomicGroupSequence })
            .HasDatabaseName("IX_KBChunks_AtomicGroup")
            .HasFilter("[AtomicGroupId] IS NOT NULL");
    }
}
