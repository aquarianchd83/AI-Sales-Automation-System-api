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
        builder.Property(a => a.RevalidationReason).HasMaxLength(500);
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

public class KnowledgeAnswerRevisionConfiguration : IEntityTypeConfiguration<KnowledgeAnswerRevision>
{
    public void Configure(EntityTypeBuilder<KnowledgeAnswerRevision> builder)
    {
        builder.ToTable("KnowledgeAnswerRevisions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.FromStatus).HasMaxLength(30);
        builder.Property(r => r.ToStatus).HasMaxLength(30);
        builder.Property(r => r.FromText).HasMaxLength(2000);
        builder.Property(r => r.ToText).HasMaxLength(2000);
        builder.Property(r => r.Note).HasMaxLength(1000);
        builder.Property(r => r.ActorEmail).HasMaxLength(256);
        builder.HasIndex(r => new { r.TenantId, r.QuestionId, r.CreatedAt });
    }
}

public class KnowledgeVersionConfiguration : IEntityTypeConfiguration<KnowledgeVersion>
{
    public void Configure(EntityTypeBuilder<KnowledgeVersion> builder)
    {
        builder.ToTable("KnowledgeVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(v => v.ServingStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(v => v.ServingError).HasMaxLength(1000);
        builder.Property(v => v.PublishedByEmail).HasMaxLength(256);
        builder.Property(v => v.Note).HasMaxLength(1000);
        builder.HasIndex(v => new { v.TenantId, v.Number }).IsUnique();
    }
}

public class KnowledgeItemConfiguration : IEntityTypeConfiguration<KnowledgeItem>
{
    public void Configure(EntityTypeBuilder<KnowledgeItem> builder)
    {
        builder.ToTable("KnowledgeItems");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Category).IsRequired().HasMaxLength(60);
        builder.Property(i => i.Question).IsRequired().HasMaxLength(500);
        builder.Property(i => i.Summary).IsRequired().HasMaxLength(2000);
        builder.Property(i => i.Detail).HasMaxLength(8000);
        builder.Property(i => i.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(i => i.Confidence).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.EvidenceJson).IsRequired();
        builder.HasOne<KnowledgeVersion>().WithMany().HasForeignKey(i => i.VersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(i => new { i.TenantId, i.VersionId });
    }
}
