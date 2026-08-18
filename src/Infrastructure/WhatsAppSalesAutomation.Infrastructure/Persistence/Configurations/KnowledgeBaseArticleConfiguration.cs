using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeBaseArticleConfiguration : IEntityTypeConfiguration<KnowledgeBaseArticle>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseArticle> builder)
    {
        builder.ToTable("KnowledgeBaseArticles");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Title).IsRequired().HasMaxLength(200);
        builder.Property(a => a.Category).HasMaxLength(100);
        builder.Property(a => a.SourceType).HasConversion<string>().HasMaxLength(10);
        builder.Property(a => a.Content).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(10);

        builder.HasIndex(a => a.Status);

        builder.HasMany(a => a.Chunks)
            .WithOne()
            .HasForeignKey(c => c.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(a => !a.IsDeleted);
    }
}
