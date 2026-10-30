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
        builder.Property(v => v.Style).IsRequired().HasMaxLength(15);
        builder.Property(v => v.PromptVersion).IsRequired().HasMaxLength(40);
        builder.Property(v => v.Provider).IsRequired().HasMaxLength(50);
        builder.Property(v => v.Model).HasMaxLength(100);
        builder.Property(v => v.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(v => v.ReviewComment).HasMaxLength(1000);
        builder.Property(v => v.ApprovedHash).HasMaxLength(64);
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
        builder.Property(s => s.ShotType).HasMaxLength(40);
        builder.Property(s => s.CameraMove).IsRequired().HasMaxLength(15);
        builder.Property(s => s.Mood).IsRequired().HasMaxLength(15);
        builder.Property(s => s.Transition).IsRequired().HasMaxLength(15);
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

public class RenderJobConfiguration : IEntityTypeConfiguration<RenderJob>
{
    public void Configure(EntityTypeBuilder<RenderJob> builder)
    {
        builder.ToTable("RenderJobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(j => j.Stage).HasMaxLength(200);
        builder.Property(j => j.AspectRatio).IsRequired().HasMaxLength(8);
        builder.Property(j => j.Mode).IsRequired().HasMaxLength(10);
        builder.Property(j => j.VoiceGender).IsRequired().HasMaxLength(10);
        builder.Property(j => j.LanguageCode).IsRequired().HasMaxLength(10);
        builder.Property(j => j.Renderer).IsRequired().HasMaxLength(30);
        builder.Property(j => j.ExternalRenderId).HasMaxLength(100);
        builder.Property(j => j.OutputStorageKey).HasMaxLength(500);
        builder.Property(j => j.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(j => j.Error).HasMaxLength(600);
        builder.HasIndex(j => new { j.TenantId, j.VersionId, j.CreatedAt });
        // "What is running now" - the per-tenant and platform-wide limits read this.
        builder.HasIndex(j => new { j.Status, j.TenantId });
    }
}

public class ContentReviewEventConfiguration : IEntityTypeConfiguration<ContentReviewEvent>
{
    public void Configure(EntityTypeBuilder<ContentReviewEvent> builder)
    {
        builder.ToTable("ContentReviewEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(15);
        builder.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(15);
        builder.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(15);
        builder.Property(e => e.ActorEmail).HasMaxLength(256);
        builder.Property(e => e.Comment).HasMaxLength(1000);
        builder.Property(e => e.SnapshotHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.VersionId, e.At });
    }
}

public class SocialConnectionConfiguration : IEntityTypeConfiguration<SocialConnection>
{
    public void Configure(EntityTypeBuilder<SocialConnection> builder)
    {
        builder.ToTable("SocialConnections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Platform).HasConversion<string>().HasMaxLength(15);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.ExternalAccountId).IsRequired().HasMaxLength(100);
        builder.Property(c => c.AccountName).IsRequired().HasMaxLength(200);
        builder.Property(c => c.EncryptedAccessToken).IsRequired();
        builder.Property(c => c.EncryptedRefreshToken).IsRequired();
        builder.Property(c => c.Scopes).IsRequired().HasMaxLength(1000);
        builder.Property(c => c.LastError).HasMaxLength(500);
        // Connecting the same channel twice updates the one row.
        builder.HasIndex(c => new { c.TenantId, c.Platform, c.ExternalAccountId }).IsUnique();
    }
}

public class PublicationConfiguration : IEntityTypeConfiguration<Publication>
{
    public void Configure(EntityTypeBuilder<Publication> builder)
    {
        builder.ToTable("Publications");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Platform).HasConversion<string>().HasMaxLength(15);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(p => p.AccountName).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Title).IsRequired().HasMaxLength(100);
        builder.Property(p => p.Description).HasMaxLength(5000);
        builder.Property(p => p.Privacy).IsRequired().HasMaxLength(10);
        builder.Property(p => p.ExternalPostId).HasMaxLength(100);
        builder.Property(p => p.Url).HasMaxLength(300);
        builder.Property(p => p.IdempotencyKey).IsRequired().HasMaxLength(100);
        builder.Property(p => p.ApprovedHash).IsRequired().HasMaxLength(64);
        builder.Property(p => p.LastError).HasMaxLength(600);
        // The same request twice is the same publication.
        builder.HasIndex(p => new { p.TenantId, p.IdempotencyKey }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.VersionId, p.CreatedAt });
        builder.HasIndex(p => new { p.Status, p.CreatedAt });
    }
}

public class PublicationMetricSnapshotConfiguration : IEntityTypeConfiguration<PublicationMetricSnapshot>
{
    public void Configure(EntityTypeBuilder<PublicationMetricSnapshot> builder)
    {
        builder.ToTable("PublicationMetricSnapshots");
        builder.HasKey(m => m.Id);
        builder.HasIndex(m => new { m.TenantId, m.PublicationId, m.CapturedAtUtc });
    }
}

public class RenderClipConfiguration : IEntityTypeConfiguration<RenderClip>
{
    public void Configure(EntityTypeBuilder<RenderClip> builder)
    {
        builder.ToTable("RenderClips");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(c => c.ExternalRenderId).HasMaxLength(100);
        builder.Property(c => c.OutputStorageKey).HasMaxLength(500);
        builder.Property(c => c.Error).HasMaxLength(600);
        builder.HasIndex(c => new { c.TenantId, c.JobId, c.Order });
    }
}
