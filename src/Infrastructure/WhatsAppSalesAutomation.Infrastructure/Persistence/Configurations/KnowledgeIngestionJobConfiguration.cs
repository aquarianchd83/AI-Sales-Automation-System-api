using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeIngestionJobConfiguration : IEntityTypeConfiguration<KnowledgeIngestionJob>
{
    public void Configure(EntityTypeBuilder<KnowledgeIngestionJob> builder)
    {
        builder.ToTable("KnowledgeIngestionJobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.State).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(j => j.ReasonCode).HasMaxLength(60);
        builder.Property(j => j.Detail).HasMaxLength(2000);
        builder.Property(j => j.SecurityFindingsJson).HasColumnType("nvarchar(max)");
        builder.Property(j => j.MetricsJson).HasColumnType("nvarchar(max)");
        builder.Property(j => j.VerificationNotes).HasMaxLength(2000);
        builder.Property(j => j.EmbeddingProvider).HasMaxLength(20);
        builder.Property(j => j.EmbeddingModel).HasMaxLength(100);

        // One job per article version: the idempotency the double-click case relies on. Unfiltered on
        // purpose, so GLOBAL articles (NULL tenant) get the same guarantee - see the equivalent note
        // on UX_KBArticleVersions_KeyVersion.
        builder.HasIndex(j => new { j.ArticleId, j.ArticleVersionNumber })
            .IsUnique()
            .HasDatabaseName("UX_KBIngestionJobs_ArticleVersion");

        builder.HasIndex(j => new { j.State, j.CreatedAt }).HasDatabaseName("IX_KBIngestionJobs_State");
    }
}
