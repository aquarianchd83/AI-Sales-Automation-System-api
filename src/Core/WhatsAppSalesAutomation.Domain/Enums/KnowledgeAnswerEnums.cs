namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Where an answer's content came from: quoted from a source, an AI's reading of sources, suggested wording, or the tenant's own.</summary>
public enum KnowledgeAnswerKind
{
    SourceExtracted = 0,
    AiInterpreted = 1,
    SuggestedLanguage = 2,
    TenantProvided = 3,
}

/// <summary>How strong the evidence is, worked out by the application from verified quotations: High (a source directly states it), Medium
/// (supported, but needs interpretation or several passages), Low (partial, indirect, unproven numbers or conflicting), NoEvidence.</summary>
public enum KnowledgeConfidence
{
    NoEvidence = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum KnowledgeVerificationStatus
{
    PendingVerification = 0,
    VerifiedAndApproved = 1,
    CorrectionRequired = 2,
    Rejected = 3,
    InformationNotAvailable = 4,

    /// <summary>The tenant says the question does not apply to their business. Drafting leaves it alone.</summary>
    NotApplicable = 5,
}

public enum KnowledgeConflictStatus
{
    Open = 0,
    Resolved = 1,
}

public enum KnowledgeDraftRunStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>What happened to an answer or a conflict - the kinds of entry in its audit trail.</summary>
public enum KnowledgeRevisionKind
{
    Drafted = 0,
    Approved = 1,
    Edited = 2,
    InformationProvided = 3,
    Rejected = 4,
    MarkedUnknown = 5,
    MarkedNotApplicable = 6,
    Reopened = 7,
    ConflictResolved = 8,

    /// <summary>A source this answer was drawn from changed or was removed: the answer needs another look.</summary>
    SourceChanged = 9,
}

public enum KnowledgeVersionStatus
{
    /// <summary>The version customers' answers are read from now.</summary>
    Current = 0,

    /// <summary>Replaced by a newer version. Kept, never deleted: it is the record of what was published and when.</summary>
    Superseded = 1,
}
