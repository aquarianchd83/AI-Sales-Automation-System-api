using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class StudioActivityConfiguration : IEntityTypeConfiguration<StudioActivity>
{
    public void Configure(EntityTypeBuilder<StudioActivity> builder)
    {
        builder.ToTable("StudioActivities");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(a => a.QuotaType).HasConversion<string>().HasMaxLength(30);
        builder.Property(a => a.Description).IsRequired().HasMaxLength(300);
        builder.Property(a => a.OperationKey).IsRequired().HasMaxLength(150);
        builder.Property(a => a.ReferenceType).HasMaxLength(50);
        builder.Property(a => a.ReferenceId).HasMaxLength(100);
        builder.Property(a => a.Provider).HasMaxLength(50);
        builder.Property(a => a.Detail).HasMaxLength(300);
        builder.Property(a => a.Units).HasColumnType("decimal(18,4)");
        builder.Property(a => a.UnitsRefunded).HasColumnType("decimal(18,4)");
        builder.Property(a => a.BalanceAfter).HasColumnType("decimal(18,4)");

        // The tenant's activity list, newest first.
        builder.HasIndex(a => new { a.TenantId, a.RequestedAtUtc });
        // The retry lookup: one charge per real-world event, ever.
        builder.HasIndex(a => new { a.TenantId, a.OperationKey }).IsUnique();
    }
}

public class TenantStudioSettingsConfiguration : IEntityTypeConfiguration<TenantStudioSettings>
{
    public void Configure(EntityTypeBuilder<TenantStudioSettings> builder)
    {
        builder.ToTable("TenantStudioSettings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ApprovalPolicy).HasConversion<string>().HasMaxLength(30);

        // At most one row per tenant.
        builder.HasIndex(s => s.TenantId).IsUnique();
    }
}

public class ContentProjectConfiguration : IEntityTypeConfiguration<ContentProject>
{
    public void Configure(EntityTypeBuilder<ContentProject> builder)
    {
        builder.ToTable("ContentProjects");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Title).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Goal).IsRequired().HasMaxLength(1000);
        builder.Property(p => p.Language).IsRequired().HasMaxLength(40);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(15);
        builder.HasIndex(p => new { p.TenantId, p.CreatedAt });
    }
}

public class ContentTopicConfiguration : IEntityTypeConfiguration<ContentTopic>
{
    public void Configure(EntityTypeBuilder<ContentTopic> builder)
    {
        builder.ToTable("ContentTopics");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Title).IsRequired().HasMaxLength(250);
        builder.Property(t => t.Rationale).HasMaxLength(1000);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(t => t.Provider).IsRequired().HasMaxLength(50);
        builder.HasIndex(t => new { t.TenantId, t.ProjectId });
    }
}

public class ContentVersionConfiguration : IEntityTypeConfiguration<ContentVersion>
{
    public void Configure(EntityTypeBuilder<ContentVersion> builder)
    {
        builder.ToTable("ContentVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(v => v.Notes).HasMaxLength(1000);
        builder.Property(v => v.PromptVersion).IsRequired().HasMaxLength(40);
        builder.Property(v => v.Provider).IsRequired().HasMaxLength(50);
        builder.Property(v => v.Model).HasMaxLength(100);
        builder.Property(v => v.ContentHash).IsRequired().HasMaxLength(64);
        // One number per project: two scripts written at the same moment cannot both be version 4.
        builder.HasIndex(v => new { v.TenantId, v.ProjectId, v.Number }).IsUnique();
    }
}

public class ContentSceneConfiguration : IEntityTypeConfiguration<ContentScene>
{
    public void Configure(EntityTypeBuilder<ContentScene> builder)
    {
        builder.ToTable("ContentScenes");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Narration).IsRequired().HasMaxLength(2000);
        builder.Property(s => s.OnScreenText).HasMaxLength(300);
        builder.Property(s => s.VisualHint).HasMaxLength(500);
        builder.Property(s => s.ClaimStatus).HasConversion<string>().HasMaxLength(15);
        builder.Property(s => s.ClaimNote).HasMaxLength(600);
        builder.HasIndex(s => new { s.TenantId, s.VersionId, s.Order });
    }
}

public class StudioAssetConfiguration : IEntityTypeConfiguration<StudioAsset>
{
    public void Configure(EntityTypeBuilder<StudioAsset> builder)
    {
        builder.ToTable("StudioAssets");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(15);
        builder.Property(a => a.Name).IsRequired().HasMaxLength(200);
        builder.Property(a => a.Description).HasMaxLength(500);
        builder.Property(a => a.Tags).HasMaxLength(300);
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(60);
        builder.Property(a => a.StorageKey).IsRequired().HasMaxLength(500);
        builder.Property(a => a.Checksum).IsRequired().HasMaxLength(64);
        builder.Property(a => a.Provider).HasMaxLength(40);
        builder.Property(a => a.ProviderAssetId).HasMaxLength(60);
        builder.Property(a => a.SourceUrl).HasMaxLength(500);
        builder.Property(a => a.LicenseName).IsRequired().HasMaxLength(100);
        builder.Property(a => a.LicenseUrl).HasMaxLength(300);
        builder.Property(a => a.Attribution).HasMaxLength(300);
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt });
        // The same file is kept once per tenant.
        builder.HasIndex(a => new { a.TenantId, a.Checksum }).IsUnique();
    }
}
