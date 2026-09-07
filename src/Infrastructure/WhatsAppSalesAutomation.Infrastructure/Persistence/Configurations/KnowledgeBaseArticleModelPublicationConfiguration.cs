using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeBaseArticleModelPublicationConfiguration : IEntityTypeConfiguration<KnowledgeBaseArticleModelPublication>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseArticleModelPublication> builder)
    {
        builder.ToTable("KnowledgeBaseArticleModelPublications");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Provider).HasConversion<string>().HasMaxLength(20);

        // One row per (article, provider) - PublishToModelAsync upserts against this rather than
        // relying on application-level de-duplication.
        builder.HasIndex(p => new { p.ArticleId, p.Provider }).IsUnique();
    }
}
