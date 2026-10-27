using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// One piece of marketing content the tenant is planning - the home of its topic ideas and the scripts written for it. A project belongs to
/// one tenant and is deleted softly: what was planned stays on record.
/// </summary>
public class ContentProject : BaseEntity, ITenantOwned, ISoftDelete
{
    public Guid TenantId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>What the tenant wants the content to achieve, in their words ("get more bookings for cataract surgery").</summary>
    public string Goal { get; set; } = string.Empty;

    /// <summary>The language the narration is written in, as a name ("English", "Hindi", "Hinglish").</summary>
    public string Language { get; set; } = "English";

    public ContentProjectStatus Status { get; set; } = ContentProjectStatus.Draft;

    /// <summary>The newest script version; older ones stay on record.</summary>
    public Guid? CurrentVersionId { get; set; }

    public Guid? CreatedBy { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>A video idea suggested for a project, with why it fits and which verified facts it can rest on.</summary>
public class ContentTopic : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Why this topic suits the goal and the business.</summary>
    public string? Rationale { get; set; }

    public ContentTopicStatus Status { get; set; } = ContentTopicStatus.Suggested;

    /// <summary>JSON array of the business-fact ids the idea rests on.</summary>
    public string FactIdsJson { get; set; } = "[]";

    /// <summary>True when the platform's AI was in test mode: the text is predefined and was not written from the tenant's facts.</summary>
    public bool IsSimulated { get; set; }

    public string Provider { get; set; } = string.Empty;
}

/// <summary>
/// One written script for a project. A new script is a new version - the earlier ones are kept. The review phase later moves a version through
/// InReview/Approved; until then every version is a draft, and a draft is never published.
/// </summary>
public class ContentVersion : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>1, 2, 3... per project.</summary>
    public int Number { get; set; }

    public Guid? TopicId { get; set; }

    public ContentVersionStatus Status { get; set; } = ContentVersionStatus.Draft;

    /// <summary>The full narration, one scene after another. Rebuilt from the scenes whenever one is edited.</summary>
    public string ScriptText { get; set; } = string.Empty;

    /// <summary>The length the tenant asked for, in seconds.</summary>
    public int TargetSeconds { get; set; }

    /// <summary>Anything the tenant asked the writer to keep in mind.</summary>
    public string? Notes { get; set; }

    /// <summary>Which version of the prompt wrote it, so a later change to the prompt is traceable.</summary>
    public string PromptVersion { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string? Model { get; set; }

    /// <summary>True when written by the platform's test mode. Such a script is predefined, not drawn from the tenant's facts, and must not be approved or published.</summary>
    public bool IsSimulated { get; set; }

    /// <summary>The studio charge that paid for it, so the tenant can see what it cost.</summary>
    public Guid? ActivityId { get; set; }

    /// <summary>SHA-256 of the scenes' text. The review phase compares it to know whether content changed after approval.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public Guid? CreatedBy { get; set; }
}

/// <summary>One scene of a script: what is said, what is shown on screen, how long it runs, and the verified facts it rests on.</summary>
public class ContentScene : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid VersionId { get; set; }

    public int Order { get; set; }

    public string Narration { get; set; } = string.Empty;

    public string? OnScreenText { get; set; }

    public int DurationSeconds { get; set; }

    /// <summary>What the picture should show, in words. The asset phase turns this into a search.</summary>
    public string? VisualHint { get; set; }

    /// <summary>JSON array of the verified business-fact ids the writer cited for this scene.</summary>
    public string FactIdsJson { get; set; } = "[]";

    /// <summary>Whether the narration is backed by a verified, current fact. Worked out by the application, never taken from the AI.</summary>
    public ClaimStatus ClaimStatus { get; set; } = ClaimStatus.NotChecked;

    /// <summary>A sentence saying why, e.g. which fact supports it or what is missing.</summary>
    public string? ClaimNote { get; set; }

    /// <summary>The picture chosen for this scene (a <see cref="StudioAsset"/> of the same tenant), or null while none is.</summary>
    public Guid? ChosenAssetId { get; set; }

    /// <summary>True when the tenant changed what the AI wrote.</summary>
    public bool EditedByTenant { get; set; }
}
