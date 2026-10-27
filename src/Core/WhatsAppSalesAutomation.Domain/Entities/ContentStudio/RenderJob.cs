using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// One attempt to turn a script version and its pictures into a video. It records what was asked (the version, its fingerprint at that moment,
/// the shape and voice), where it stands, what it cost, and where the finished file is kept. A job is never reused: asking again makes a new
/// one, so the record of every video that was made - and every one that failed - stays whole.
/// </summary>
public class RenderJob : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid VersionId { get; set; }

    public RenderJobStatus Status { get; set; } = RenderJobStatus.Queued;

    /// <summary>0-100.</summary>
    public int Progress { get; set; }

    /// <summary>A short sentence about what is happening now, for the tenant.</summary>
    public string? Stage { get; set; }

    /// <summary>"16:9", "9:16" or "1:1".</summary>
    public string AspectRatio { get; set; } = "16:9";

    /// <summary>"Female" or "Male".</summary>
    public string VoiceGender { get; set; } = "Female";

    /// <summary>The language the voice speaks, as a BCP-47 code ("en-IN").</summary>
    public string LanguageCode { get; set; } = "en-IN";

    /// <summary>Who makes the video ("Shotstack"), or "Simulator" when the platform is in test mode.</summary>
    public string Renderer { get; set; } = string.Empty;

    /// <summary>True when made in the platform's test mode: no video exists, and none is pretended.</summary>
    public bool IsSimulated { get; set; }

    /// <summary>True when the narration was spoken by a synthetic voice - always disclosed with the video.</summary>
    public bool IsAiVoice { get; set; }

    /// <summary>The renderer's own id for the job, used to ask how it is going.</summary>
    public string? ExternalRenderId { get; set; }

    /// <summary>The finished video in the tenant's private folder; null until it succeeds (and always for a test render).</summary>
    public string? OutputStorageKey { get; set; }

    public long OutputBytes { get; set; }

    public double OutputSeconds { get; set; }

    /// <summary>JSON array of the voice-over files kept while the render runs; removed when it ends.</summary>
    public string AudioKeysJson { get; set; } = "[]";

    /// <summary>The script's fingerprint when the render began: which words this video was made from.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Scenes that carried a claim no verified fact backed when the render began. A render is a preview; publishing is gated separately.</summary>
    public int FlaggedScenes { get; set; }

    public string? Error { get; set; }

    public Guid? RenderActivityId { get; set; }

    public Guid? VoiceActivityId { get; set; }

    public bool CancelRequested { get; set; }

    /// <summary>Times the renderer was asked how it is going that failed to answer; too many ends the job.</summary>
    public int PollFailures { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
