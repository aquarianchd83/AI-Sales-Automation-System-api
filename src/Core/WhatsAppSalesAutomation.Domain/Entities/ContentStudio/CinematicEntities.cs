using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>A reusable story character. Tenant-owned; images live in <see cref="StoryCharacterReference"/>, never on this row.</summary>
public class StoryCharacter : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Personality { get; set; }

    public string? VisualAttributes { get; set; }

    /// <summary>Voice and accent notes handed to the speech step ("warm, elderly, calm Hindi").</summary>
    public string? VoicePreferences { get; set; }

    public StoryCharacterOrigin Origin { get; set; }

    public StoryCharacterVisibility Visibility { get; set; } = StoryCharacterVisibility.Private;

    /// <summary>The uploader confirmed they hold the rights to the images. Required for uploads; recorded with who and when.</summary>
    public bool RightsConfirmed { get; set; }

    public Guid? RightsConfirmedBy { get; set; }

    public DateTime? RightsConfirmedAtUtc { get; set; }

    public bool IsArchived { get; set; }

    public Guid? CreatedBy { get; set; }
}

/// <summary>One reference image of a character (an S3 key, not the bytes).</summary>
public class StoryCharacterReference : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid CharacterId { get; set; }

    public string StorageKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? Checksum { get; set; }

    public StoryReferenceStatus Status { get; set; }

    /// <summary>1, 2, 3... per character. A revision is a new version; the approved one is never overwritten.</summary>
    public int Version { get; set; }

    /// <summary>The prompt that produced a generated image (for audit and for revisions).</summary>
    public string? GenerationPrompt { get; set; }
}

public class StoryProject : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>The user's topic, problem or story idea.</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>Hindi, English, Hinglish or another configured language.</summary>
    public string Language { get; set; } = "Hinglish";

    public string? Style { get; set; }

    public string? Audience { get; set; }

    public string? Moral { get; set; }

    public int TargetSeconds { get; set; } = 30;

    public StoryProjectStatus Status { get; set; }

    public SceneVideoProviderKind VideoProvider { get; set; } = SceneVideoProviderKind.Simulator;

    /// <summary>Optional spend cap in USD for this project; generation refuses to start past it.</summary>
    public decimal? BudgetUsd { get; set; }

    public Guid? CurrentScriptVersionId { get; set; }

    public Guid? CreatedBy { get; set; }
}

public class StoryProjectCharacter : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid CharacterId { get; set; }
}

public class StoryScriptVersion : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public int Number { get; set; }

    /// <summary>The full script package as JSON (see CinematicScript). Validated before it is saved.</summary>
    public string Json { get; set; } = string.Empty;

    /// <summary>Hash of the words, speakers and timing. Video generation is allowed only for an approved version.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public StoryScriptStatus Status { get; set; }

    /// <summary>"Simulator", "OpenAI" or "Edited": who wrote this version.</summary>
    public string Source { get; set; } = string.Empty;

    public Guid? ApprovedBy { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }
}

public class StoryScene : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid ScriptVersionId { get; set; }

    /// <summary>1-based position.</summary>
    public int Index { get; set; }

    public int TargetSeconds { get; set; }

    /// <summary>What the clip actually lasts, once known. Providers rarely return exactly the target.</summary>
    public int? ActualMilliseconds { get; set; }

    public string VideoPrompt { get; set; } = string.Empty;

    public StorySceneStatus Status { get; set; }

    public Guid? ClipAssetId { get; set; }

    public string? FlagReason { get; set; }
}

public class StoryDialogueLine : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid SceneId { get; set; }

    public int Order { get; set; }

    public Guid? CharacterId { get; set; }

    /// <summary>The speaker's name as written in the script, kept when no character row matches.</summary>
    public string SpeakerName { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public int StartMs { get; set; }

    public int EndMs { get; set; }

    public Guid? AudioAssetId { get; set; }
}

/// <summary>A file the story produced or consumed. Bytes live in S3; this row is the metadata.</summary>
public class StoryMediaAsset : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public StoryMediaKind Kind { get; set; }

    public string StorageKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? DurationMs { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public double? FramesPerSecond { get; set; }

    public string? Checksum { get; set; }

    /// <summary>Originals are kept for recovery; intermediates may be deleted after the retention window.</summary>
    public bool IsOriginal { get; set; } = true;
}

/// <summary>One unit of asynchronous work. The row is the truth; the Hangfire job only carries its id.</summary>
public class StoryGenerationJob : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid? SceneId { get; set; }

    public StoryJobKind Kind { get; set; }

    public StoryJobStatus Status { get; set; }

    public int Attempts { get; set; }

    public string? Provider { get; set; }

    /// <summary>The provider's own job id. Polling resumes from this after a restart, which is what prevents a second, billed submission.</summary>
    public string? ProviderJobId { get; set; }

    /// <summary>One per real-world request; a retry with the same key never submits or charges twice.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public decimal? EstimatedCostUsd { get; set; }

    public decimal? ActualCostUsd { get; set; }

    public string? Error { get; set; }

    /// <summary>The metering activity that charged this job, so a failure can give the units back.</summary>
    public Guid? ChargeActivityId { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }
}

public class StoryRender : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public StoryJobStatus Status { get; set; }

    public Guid? OutputAssetId { get; set; }

    /// <summary>Hash of the script version the render was made from.</summary>
    public string? ContentHash { get; set; }

    /// <summary>ffprobe results checked against the 9:16 / duration / audio rules, as JSON.</summary>
    public string? ValidationJson { get; set; }

    public string? Error { get; set; }
}
