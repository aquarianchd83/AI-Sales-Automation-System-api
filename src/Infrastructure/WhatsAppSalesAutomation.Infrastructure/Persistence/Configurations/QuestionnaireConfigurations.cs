using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class QuestionnaireTemplateConfiguration : IEntityTypeConfiguration<QuestionnaireTemplate>
{
    public void Configure(EntityTypeBuilder<QuestionnaireTemplate> builder)
    {
        builder.ToTable("QuestionnaireTemplates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Key).IsRequired().HasMaxLength(60);
        builder.Property(t => t.Title).IsRequired().HasMaxLength(150);
        builder.Property(t => t.Description).HasMaxLength(500);
        builder.Property(t => t.Industry).HasMaxLength(100);

        builder.HasIndex(t => t.Key).IsUnique();
    }
}

public class QuestionnaireTemplateVersionConfiguration : IEntityTypeConfiguration<QuestionnaireTemplateVersion>
{
    public void Configure(EntityTypeBuilder<QuestionnaireTemplateVersion> builder)
    {
        builder.ToTable("QuestionnaireTemplateVersions");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(v => v.DefinitionJson).IsRequired();

        builder.HasOne<QuestionnaireTemplate>().WithMany().HasForeignKey(v => v.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(v => new { v.TemplateId, v.Number }).IsUnique();
    }
}

public class QuestionnaireResponseConfiguration : IEntityTypeConfiguration<QuestionnaireResponse>
{
    public void Configure(EntityTypeBuilder<QuestionnaireResponse> builder)
    {
        builder.ToTable("QuestionnaireResponses");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(r => r.AnswersJson).IsRequired();

        // No cascade: a version a tenant answered is part of the tenant's record and is never deleted from under it.
        builder.HasOne<QuestionnaireTemplateVersion>().WithMany().HasForeignKey(r => r.VersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<QuestionnaireTemplate>().WithMany().HasForeignKey(r => r.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.CreatedAt });
    }
}

public class BusinessFactConfiguration : IEntityTypeConfiguration<BusinessFact>
{
    public void Configure(EntityTypeBuilder<BusinessFact> builder)
    {
        builder.ToTable("BusinessFacts");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Category).IsRequired().HasMaxLength(60);
        // Wide enough for a fact built from the longest allowed answer (QuestionnaireEngine.MaxFactStatementLength); a fact a person types is held to 1,000.
        builder.Property(f => f.Statement).IsRequired().HasMaxLength(QuestionnaireEngine.MaxFactStatementLength);
        builder.Property(f => f.SourceType).HasConversion<string>().HasMaxLength(15);
        builder.Property(f => f.SourceRef).HasMaxLength(60);
        builder.Property(f => f.Subject).HasMaxLength(120);
        builder.Property(f => f.Context).HasMaxLength(300);
        builder.Property(f => f.Evidence).HasMaxLength(500);
        builder.Property(f => f.Origin).HasConversion<string>().HasMaxLength(20).HasDefaultValue(BusinessFactOrigin.TenantProvided);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(15);

        builder.HasIndex(f => new { f.TenantId, f.Status, f.Category });
        builder.HasIndex(f => new { f.TenantId, f.SourceType, f.SourceRef });
    }
}

public class BusinessFactRevisionConfiguration : IEntityTypeConfiguration<BusinessFactRevision>
{
    public void Configure(EntityTypeBuilder<BusinessFactRevision> builder)
    {
        builder.ToTable("BusinessFactRevisions");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Category).IsRequired().HasMaxLength(60);
        builder.Property(r => r.Subject).HasMaxLength(120);
        builder.Property(r => r.Statement).IsRequired().HasMaxLength(QuestionnaireEngine.MaxFactStatementLength);
        builder.Property(r => r.Context).HasMaxLength(300);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(r => r.Note).HasMaxLength(300);

        // The history is part of the fact's record: deleting a fact is not a thing, and neither is deleting its history from under it.
        builder.HasOne<BusinessFact>().WithMany().HasForeignKey(r => r.FactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.FactId, r.Number }).IsUnique();
    }
}

public class QuestionnaireAnswerRevisionConfiguration : IEntityTypeConfiguration<QuestionnaireAnswerRevision>
{
    public void Configure(EntityTypeBuilder<QuestionnaireAnswerRevision> builder)
    {
        builder.ToTable("QuestionnaireAnswerRevisions");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.QuestionKey).IsRequired().HasMaxLength(60);
        builder.Property(r => r.Kind).IsRequired().HasMaxLength(15);

        builder.HasOne<QuestionnaireResponse>().WithMany().HasForeignKey(r => r.ResponseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.ResponseId, r.CreatedAt });
    }
}

public class KnowledgeGapDecisionConfiguration : IEntityTypeConfiguration<KnowledgeGapDecision>
{
    public void Configure(EntityTypeBuilder<KnowledgeGapDecision> builder)
    {
        builder.ToTable("KnowledgeGapDecisions");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.RequirementKey).IsRequired().HasMaxLength(60);
        builder.Property(d => d.Kind).HasConversion<string>().HasMaxLength(15);

        builder.HasIndex(d => new { d.TenantId, d.RequirementKey }).IsUnique();
    }
}
