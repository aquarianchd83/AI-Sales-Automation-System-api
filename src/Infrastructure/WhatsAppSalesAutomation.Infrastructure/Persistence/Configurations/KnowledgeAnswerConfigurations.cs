using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class KnowledgeQuestionConfiguration : IEntityTypeConfiguration<KnowledgeQuestion>
{
    public void Configure(EntityTypeBuilder<KnowledgeQuestion> builder)
    {
        builder.ToTable("KnowledgeQuestions");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Key).HasMaxLength(60);
        builder.Property(q => q.Text).IsRequired().HasMaxLength(500);
        builder.Property(q => q.Category).IsRequired().HasMaxLength(60);
        builder.Property(q => q.ClarifyingQuestion).HasMaxLength(500);
        builder.HasIndex(q => new { q.TenantId, q.SortOrder });
        builder.HasIndex(q => new { q.TenantId, q.Key });
    }
}

public class KnowledgeAnswerConfiguration : IEntityTypeConfiguration<KnowledgeAnswer>
{
    public void Configure(EntityTypeBuilder<KnowledgeAnswer> builder)
    {
        builder.ToTable("KnowledgeAnswers");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Summary).HasMaxLength(2000);
        builder.Property(a => a.Detail).HasMaxLength(8000);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(a => a.Confidence).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(a => a.Limitation).HasMaxLength(2000);
        builder.Property(a => a.ConfidenceReasons).HasMaxLength(1500);
        builder.Property(a => a.ClarifyingQuestion).HasMaxLength(500);
        builder.Property(a => a.Provider).IsRequired().HasMaxLength(20);
        builder.Property(a => a.Model).HasMaxLength(100);
        builder.Property(a => a.SourceVersions).HasMaxLength(4000);
        builder.HasOne<KnowledgeQuestion>().WithMany().HasForeignKey(a => a.QuestionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.TenantId, a.QuestionId, a.IsCurrent });
    }
}

public class AnswerEvidenceConfiguration : IEntityTypeConfiguration<AnswerEvidence>
{
    public void Configure(EntityTypeBuilder<AnswerEvidence> builder)
    {
        builder.ToTable("AnswerEvidences");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Excerpt).IsRequired().HasMaxLength(2000);
        builder.Property(e => e.SourceName).IsRequired().HasMaxLength(2048);
        builder.Property(e => e.Location).IsRequired().HasMaxLength(2048);
        builder.Property(e => e.Heading).HasMaxLength(300);
        builder.Property(e => e.PassageHash).IsRequired().HasMaxLength(64);
        builder.HasOne<KnowledgeAnswer>().WithMany().HasForeignKey(e => e.AnswerId).OnDelete(DeleteBehavior.Cascade);
        // No foreign key to the passage: a re-scan replaces passages, and the evidence keeps what was quoted and where.
        builder.HasIndex(e => new { e.TenantId, e.AnswerId });
        builder.HasIndex(e => new { e.TenantId, e.SourceId });
    }
}

public class KnowledgeConflictConfiguration : IEntityTypeConfiguration<KnowledgeConflict>
{
    public void Configure(EntityTypeBuilder<KnowledgeConflict> builder)
    {
        builder.ToTable("KnowledgeConflicts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Fact).IsRequired().HasMaxLength(100);
        builder.Property(c => c.ValuesJson).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.ChosenValue).HasMaxLength(500);
        builder.HasIndex(c => new { c.TenantId, c.AnswerId });
    }
}

public class FeatureBenefitConfiguration : IEntityTypeConfiguration<FeatureBenefit>
{
    public void Configure(EntityTypeBuilder<FeatureBenefit> builder)
    {
        builder.ToTable("FeatureBenefits");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Feature).IsRequired().HasMaxLength(300);
        builder.Property(f => f.Problem).HasMaxLength(1000);
        builder.Property(f => f.Benefit).HasMaxLength(1500);
        builder.Property(f => f.Segment).HasMaxLength(300);
        builder.Property(f => f.Confidence).HasConversion<string>().HasMaxLength(20);
        builder.Property(f => f.EvidenceJson).IsRequired();
        builder.Property(f => f.Provider).IsRequired().HasMaxLength(20);
        builder.HasIndex(f => new { f.TenantId, f.RunVersion });
    }
}

public class KnowledgeDraftRunConfiguration : IEntityTypeConfiguration<KnowledgeDraftRun>
{
    public void Configure(EntityTypeBuilder<KnowledgeDraftRun> builder)
    {
        builder.ToTable("KnowledgeDraftRuns");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Error).HasMaxLength(1000);
        builder.HasIndex(r => new { r.TenantId, r.CreatedAt });
    }
}
