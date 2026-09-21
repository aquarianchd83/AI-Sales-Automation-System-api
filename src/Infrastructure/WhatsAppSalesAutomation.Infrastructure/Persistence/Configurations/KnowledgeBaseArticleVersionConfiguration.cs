using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeBaseArticleVersionConfiguration : IEntityTypeConfiguration<KnowledgeBaseArticleVersion>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseArticleVersion> builder)
    {
        builder.ToTable("KnowledgeBaseArticleVersions");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.ArticleKey).IsRequired().HasMaxLength(200);
        builder.Property(v => v.Title).IsRequired().HasMaxLength(200);
        builder.Property(v => v.Content).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(v => v.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(v => v.MetadataJson).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(v => v.ChangeNote).HasMaxLength(1000);

        // The lineage read: every version of one key, newest first. Unique because publishing the
        // same version number twice for one key would make "what did v3 say" ambiguous, which is the
        // one question this table exists to answer.
        // The lineage read: every version of one key, newest first. Unique because publishing the
        // same version number twice for one key would make "what did v3 say" ambiguous, which is the
        // one question this table exists to answer.
        //
        // HasFilter(null) is load-bearing. EF Core adds "WHERE [TenantId] IS NOT NULL" to any unique
        // index over a nullable column, which would have exempted every GLOBAL version from the
        // uniqueness - the platform's own policy history being the part least able to afford a
        // duplicate. SQL Server treats NULLs as equal for uniqueness, so an unfiltered index gives
        // GLOBAL rows exactly the guarantee tenant rows get.
        builder.HasIndex(v => new { v.TenantId, v.ArticleKey, v.VersionNumber })
            .IsUnique()
            .HasDatabaseName("UX_KBArticleVersions_KeyVersion")
            .HasFilter(null);

        // The audit read: "what did the article the AI cited say at the time", by article id.
        builder.HasIndex(v => new { v.ArticleId, v.VersionNumber })
            .HasDatabaseName("IX_KBArticleVersions_Article");

        // Deliberately NO foreign key to KnowledgeBaseArticles. A cascade would delete the history
        // along with the article, at exactly the moment the history is most likely to be wanted, and
        // a restrict would block an article from ever being hard-deleted. Orphaned history is the
        // right trade for an append-only audit table - see the entity's own doc comment.
    }
}
