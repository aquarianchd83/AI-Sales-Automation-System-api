using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeSourceConfiguration : IEntityTypeConfiguration<KnowledgeSource>
{
    public void Configure(EntityTypeBuilder<KnowledgeSource> builder)
    {
        builder.ToTable("KnowledgeSources");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(300);
        builder.Property(s => s.Url).HasMaxLength(2048);
        builder.Property(s => s.FileName).HasMaxLength(300);
        builder.Property(s => s.ContentType).HasMaxLength(150);
        builder.Property(s => s.ContentHash).HasMaxLength(64);
        builder.Property(s => s.StorageKey).HasMaxLength(500);
        builder.Property(s => s.TextStorageKey).HasMaxLength(500);
        builder.Property(s => s.ErrorCode).HasMaxLength(60);
        builder.Property(s => s.Error).HasMaxLength(1000);
        builder.Property(s => s.Warnings).HasMaxLength(2000);

        builder.HasIndex(s => new { s.TenantId, s.CreatedAt });
        builder.HasIndex(s => new { s.TenantId, s.ContentHash });
        builder.HasIndex(s => new { s.TenantId, s.Url });
    }
}

public class KnowledgeSourcePageConfiguration : IEntityTypeConfiguration<KnowledgeSourcePage>
{
    public void Configure(EntityTypeBuilder<KnowledgeSourcePage> builder)
    {
        builder.ToTable("KnowledgeSourcePages");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Url).IsRequired().HasMaxLength(2048);
        builder.Property(p => p.Title).HasMaxLength(300);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Error).HasMaxLength(500);
        builder.Property(p => p.ContentHash).HasMaxLength(64);

        builder.HasOne<KnowledgeSource>().WithMany().HasForeignKey(p => p.SourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => new { p.TenantId, p.SourceId, p.SourceVersion });
    }
}

public class SourcePassageConfiguration : IEntityTypeConfiguration<SourcePassage>
{
    public void Configure(EntityTypeBuilder<SourcePassage> builder)
    {
        builder.ToTable("SourcePassages");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Url).HasMaxLength(2048);
        builder.Property(p => p.Heading).HasMaxLength(300);
        builder.Property(p => p.Text).IsRequired();
        builder.Property(p => p.Hash).IsRequired().HasMaxLength(64);

        builder.HasOne<KnowledgeSource>().WithMany().HasForeignKey(p => p.SourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => new { p.TenantId, p.SourceId, p.SourceVersion, p.Order });
        builder.HasIndex(p => new { p.TenantId, p.Hash });
    }
}
