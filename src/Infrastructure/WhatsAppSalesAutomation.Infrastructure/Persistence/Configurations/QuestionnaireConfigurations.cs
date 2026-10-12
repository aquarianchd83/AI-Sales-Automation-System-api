using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

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
        builder.Property(f => f.Statement).IsRequired().HasMaxLength(1000);
        builder.Property(f => f.SourceType).HasConversion<string>().HasMaxLength(15);
        builder.Property(f => f.SourceRef).HasMaxLength(60);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(15);

        builder.HasIndex(f => new { f.TenantId, f.Status, f.Category });
        builder.HasIndex(f => new { f.TenantId, f.SourceType, f.SourceRef });
    }
}
