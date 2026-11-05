using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class StoryCharacterConfiguration : IEntityTypeConfiguration<StoryCharacter>
{
    public void Configure(EntityTypeBuilder<StoryCharacter> builder)
    {
        builder.ToTable("StoryCharacters");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).IsRequired().HasMaxLength(150);
        builder.Property(c => c.Description).HasMaxLength(2000);
        builder.Property(c => c.Personality).HasMaxLength(1000);
        builder.Property(c => c.VisualAttributes).HasMaxLength(2000);
        builder.Property(c => c.VoicePreferences).HasMaxLength(500);
        builder.Property(c => c.Origin).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.Visibility).HasConversion<string>().HasMaxLength(10);
        builder.HasIndex(c => new { c.TenantId, c.Name });
    }
}

public class StoryCharacterReferenceConfiguration : IEntityTypeConfiguration<StoryCharacterReference>
{
    public void Configure(EntityTypeBuilder<StoryCharacterReference> builder)
    {
        builder.ToTable("StoryCharacterReferences");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.StorageKey).IsRequired().HasMaxLength(500);
        builder.Property(r => r.ContentType).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Checksum).HasMaxLength(100);
        builder.Property(r => r.GenerationPrompt).HasMaxLength(4000);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(15);
        builder.HasIndex(r => new { r.CharacterId, r.Version }).IsUnique();
    }
}

public class StoryProjectConfiguration : IEntityTypeConfiguration<StoryProject>
{
    public void Configure(EntityTypeBuilder<StoryProject> builder)
    {
        builder.ToTable("StoryProjects");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Title).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Topic).IsRequired().HasMaxLength(4000);
        builder.Property(p => p.Language).IsRequired().HasMaxLength(30);
        builder.Property(p => p.Style).HasMaxLength(200);
        builder.Property(p => p.Audience).HasMaxLength(200);
        builder.Property(p => p.Moral).HasMaxLength(500);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.VideoProvider).HasConversion<string>().HasMaxLength(15);
        builder.Property(p => p.BudgetUsd).HasColumnType("decimal(18,4)");
        builder.HasIndex(p => new { p.TenantId, p.CreatedAt });
    }
}

public class StoryProjectCharacterConfiguration : IEntityTypeConfiguration<StoryProjectCharacter>
{
    public void Configure(EntityTypeBuilder<StoryProjectCharacter> builder)
    {
        builder.ToTable("StoryProjectCharacters");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => new { c.ProjectId, c.CharacterId }).IsUnique();
    }
}

public class StoryScriptVersionConfiguration : IEntityTypeConfiguration<StoryScriptVersion>
{
    public void Configure(EntityTypeBuilder<StoryScriptVersion> builder)
    {
        builder.ToTable("StoryScriptVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Json).IsRequired();
        builder.Property(v => v.ContentHash).IsRequired().HasMaxLength(100);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(v => v.Source).IsRequired().HasMaxLength(30);
        builder.HasIndex(v => new { v.ProjectId, v.Number }).IsUnique();
    }
}

public class StorySceneConfiguration : IEntityTypeConfiguration<StoryScene>
{
    public void Configure(EntityTypeBuilder<StoryScene> builder)
    {
        builder.ToTable("StoryScenes");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.VideoPrompt).IsRequired().HasMaxLength(4000);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.FlagReason).HasMaxLength(500);
        builder.HasIndex(s => new { s.ProjectId, s.ScriptVersionId, s.Index }).IsUnique();
    }
}

public class StoryDialogueLineConfiguration : IEntityTypeConfiguration<StoryDialogueLine>
{
    public void Configure(EntityTypeBuilder<StoryDialogueLine> builder)
    {
        builder.ToTable("StoryDialogueLines");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.SpeakerName).IsRequired().HasMaxLength(150);
        builder.Property(l => l.Text).IsRequired().HasMaxLength(2000);
        builder.HasIndex(l => new { l.SceneId, l.Order }).IsUnique();
    }
}

public class StoryMediaAssetConfiguration : IEntityTypeConfiguration<StoryMediaAsset>
{
    public void Configure(EntityTypeBuilder<StoryMediaAsset> builder)
    {
        builder.ToTable("StoryMediaAssets");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.StorageKey).IsRequired().HasMaxLength(500);
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(100);
        builder.Property(a => a.Checksum).HasMaxLength(100);
        builder.HasIndex(a => new { a.ProjectId, a.Kind });
    }
}

public class StoryGenerationJobConfiguration : IEntityTypeConfiguration<StoryGenerationJob>
{
    public void Configure(EntityTypeBuilder<StoryGenerationJob> builder)
    {
        builder.ToTable("StoryGenerationJobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(j => j.Provider).HasMaxLength(30);
        builder.Property(j => j.ProviderJobId).HasMaxLength(300);
        builder.Property(j => j.IdempotencyKey).IsRequired().HasMaxLength(150);
        builder.Property(j => j.Error).HasMaxLength(2000);
        builder.Property(j => j.EstimatedCostUsd).HasColumnType("decimal(18,4)");
        builder.Property(j => j.ActualCostUsd).HasColumnType("decimal(18,4)");
        // One job per real-world request, ever: the guard against duplicate submission and billing.
        builder.HasIndex(j => new { j.TenantId, j.IdempotencyKey }).IsUnique();
        builder.HasIndex(j => new { j.ProjectId, j.Status });
    }
}

public class StoryPublicationConfiguration : IEntityTypeConfiguration<StoryPublication>
{
    public void Configure(EntityTypeBuilder<StoryPublication> builder)
    {
        builder.ToTable("StoryPublications");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Platform).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Url).IsRequired().HasMaxLength(1000);
        builder.HasIndex(p => p.ProjectId);
    }
}

public class StoryRenderConfiguration : IEntityTypeConfiguration<StoryRender>
{
    public void Configure(EntityTypeBuilder<StoryRender> builder)
    {
        builder.ToTable("StoryRenders");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(r => r.ContentHash).HasMaxLength(100);
        builder.Property(r => r.Error).HasMaxLength(2000);
        builder.HasIndex(r => r.ProjectId);
    }
}
