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
    Manual = 1
}
