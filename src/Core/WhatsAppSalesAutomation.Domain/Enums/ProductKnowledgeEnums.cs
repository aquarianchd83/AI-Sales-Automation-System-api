namespace WhatsAppSalesAutomation.Domain.Enums;

public enum KnowledgeSourceKind
{
    Document = 0,
    Website = 1,
}

/// <summary>Queued → Processing → Ready, or Failed (with a reason and a Retry).</summary>
public enum KnowledgeSourceStatus
{
    Queued = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3,
}

public enum KnowledgeSourcePageStatus
{
    Read = 0,
    Duplicate = 1,
    Blocked = 2,
    Failed = 3,
    Skipped = 4,
}
