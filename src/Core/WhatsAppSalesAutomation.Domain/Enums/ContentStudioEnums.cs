namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What a Content Studio activity row is about. Stored as a string, so adding a kind never renumbers
/// history. Every metered step of the studio has a kind, which is what lets the tenant's activity screen say
/// what a charge was for.</summary>
public enum StudioActivityKind
{
    TopicIdeas = 0,
    Script = 1,
    SceneBreakdown = 2,
    VoiceOver = 3,
    StockAssetSearch = 4,
    ImageGeneration = 5,
    VideoRender = 6,
    Publish = 7,
    Other = 8
}

/// <summary>Where one metered studio activity stands.</summary>
public enum StudioActivityStatus
{
    /// <summary>Units are spent and the work is under way.</summary>
    Charged = 0,

    /// <summary>The work finished; the units stay spent.</summary>
    Completed = 1,

    /// <summary>The work failed on our or the provider's side and the units were given back.</summary>
    Refunded = 2,

    /// <summary>Refused before anything was spent (no quota left).</summary>
    Blocked = 3
}

/// <summary>Who may approve studio content before it can be published. A tenant setting, so the owner can widen or
/// narrow it at any time; the default is the narrowest.</summary>
public enum StudioApprovalPolicy
{
    AdminOnly = 0,
    AdminAndSalesManager = 1
}

/// <summary>How studio files are served. PublicBucket is the platform's existing public bucket (a file is readable by
/// anyone holding its unguessable address); SignedUrls hands out short-lived addresses instead.</summary>
public enum StudioStorageMode
{
    PublicBucket = 0,
    SignedUrls = 1
}
/// <summary>Where one version of a questionnaire template stands. Only a Published version is ever shown to a tenant, and a published
/// version is never edited - a change is a new version - so an answer always means what it meant when it was given.</summary>
public enum QuestionnaireVersionStatus
{
    Draft = 0,
    Published = 1
}

public enum QuestionnaireResponseStatus
{
    InProgress = 0,
    Completed = 1
}

public enum BusinessFactStatus
{
    /// <summary>Stated but not yet checked by a person. Content may not rely on it.</summary>
    Draft = 0,

    /// <summary>A person with authority has confirmed it is true. The only status content may assert.</summary>
    Verified = 1,

    /// <summary>No longer true or no longer wanted. Kept for the record, never used.</summary>
    Retired = 2
}

public enum BusinessFactSource
{
    Questionnaire = 0,
    Manual = 1,

    /// <summary>The tenant's answer to a follow-up question the readiness check asked to fill a gap.</summary>
    FollowUp = 2
}

/// <summary>Who stands behind the wording of a fact. Whatever the origin, only a person verifying it makes it a Verified fact - an
/// inference by the system or an AI model is a proposal until then and is never counted, retrieved or asserted as verified.</summary>
public enum BusinessFactOrigin
{
    /// <summary>The tenant said it (in the questionnaire or by typing it).</summary>
    TenantProvided = 0,

    /// <summary>The system worked it out from other data the tenant gave, by a fixed rule.</summary>
    SystemDerived = 1,

    /// <summary>An AI model inferred it. Never trusted without a person's confirmation.</summary>
    AiInferred = 2
}

/// <summary>What happened to a fact, for its revision history.</summary>
public enum BusinessFactChangeKind
{
    Created = 0,
    Edited = 1,
    Verified = 2,

    /// <summary>A verified fact went back to Draft because what it states (or the answer behind it) changed.</summary>
    Invalidated = 3,
    Retired = 4,
    DetailsChanged = 5
}

/// <summary>What kind of knowledge a fact is right now - worked out from its status, origin and dates, never stored, so it cannot go stale.
/// Only <see cref="TenantApproved"/> may be asserted in content.</summary>
public enum KnowledgeClassification
{
    /// <summary>The tenant said it; nobody has confirmed it yet.</summary>
    TenantProvided = 0,

    /// <summary>A person confirmed it is true and it has not expired.</summary>
    TenantApproved = 1,

    /// <summary>The system derived it by a fixed rule; unconfirmed.</summary>
    SystemDerived = 2,

    /// <summary>An AI inference, or any other claim nobody has stood behind. Never used.</summary>
    UnverifiedClaim = 3,

    /// <summary>Was confirmed, but its date has passed: out of date until confirmed again.</summary>
    Expired = 4,

    Retired = 5
}

/// <summary>What a tenant decided to do about a gap in its knowledge instead of filling it now.</summary>
public enum KnowledgeGapDecisionKind
{
    /// <summary>"We don't need this." The gap stops blocking and stops counting against readiness.</summary>
    Skipped = 0,

    /// <summary>"Ask me later." Hidden until the chosen date, then it comes back.</summary>
    Deferred = 1
}

/// <summary>Whether an AI task ran against a real provider or the built-in simulator. A simulator result is never presented as live.</summary>
public enum AiExecutionMode
{
    Live = 0,
    Simulator = 1
}

public enum AiExecutionStatus
{
    Succeeded = 0,
    Failed = 1
}

/// <summary>Where a content project stands.</summary>
public enum ContentProjectStatus
{
    /// <summary>Planning: topics are being chosen.</summary>
    Draft = 0,

    /// <summary>At least one script has been written.</summary>
    Scripted = 1,

    Archived = 2
}

public enum ContentTopicStatus
{
    Suggested = 0,
    Chosen = 1,
    Rejected = 2
}

/// <summary>The life of one script. Planning only ever produces Draft; the review phase moves it on.</summary>
public enum ContentVersionStatus
{
    Draft = 0,
    InReview = 1,
    Approved = 2,
    Rejected = 3
}

/// <summary>Whether a scene's narration is backed by what the tenant has verified.</summary>
public enum ClaimStatus
{
    NotChecked = 0,

    /// <summary>The scene states nothing that needs a fact behind it (a greeting, a call to action, a question).</summary>
    NoClaim = 1,

    /// <summary>A verified, current fact backs it.</summary>
    Supported = 2,

    /// <summary>The tenant said something similar but nobody has verified it, or it lacks proof.</summary>
    Unverified = 3,

    /// <summary>Nothing the tenant has told us backs it.</summary>
    Unsupported = 4
}

/// <summary>What kind of picture an asset is. Always shown to the tenant and carried into the finished video's record.</summary>
public enum StudioAssetKind
{
    /// <summary>A picture or photo the tenant uploaded.</summary>
    Upload = 0,

    /// <summary>A screenshot of the tenant's own app or site, uploaded by the tenant (the studio never takes screenshots itself).</summary>
    Screenshot = 1,

    /// <summary>A photo from a stock library, copied to the tenant's folder when chosen.</summary>
    Stock = 2
}
