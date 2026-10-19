using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;

/// <summary>
/// A question the product-knowledge engine answers from the tenant's sources: one of the standard benefit and customer-value questions
/// (created for every tenant the first time they draft) or one the tenant wrote themselves. Custom questions belong to their tenant only.
/// </summary>
public class KnowledgeQuestion : BaseEntity, ITenantOwned, ISoftDelete
{
    public Guid TenantId { get; set; }

    /// <summary>A stable key for a standard question ("main-benefit"); null for a custom one.</summary>
    public string? Key { get; set; }

    public string Text { get; set; } = string.Empty;

    public bool IsStandard { get; set; }

    /// <summary>The product-knowledge category the answer belongs to ("Benefits", "Pricing"...), used later to route chat questions.</summary>
    public string Category { get; set; } = "General";

    /// <summary>What to ask the tenant when the sources cannot answer it.</summary>
    public string? ClarifyingQuestion { get; set; }

    public int SortOrder { get; set; }

    public Guid? CreatedBy { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// The current draft answer to a question, with everything needed to trust it or not: where it came from, how strong the evidence is
/// (worked out by the application, never taken from the model), what is unproven, and who/what produced it. A new suggestion replaces the
/// current one (the earlier one is kept, marked not current) - an approved answer is never replaced silently.
/// </summary>
public class KnowledgeAnswer : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid QuestionId { get; set; }

    /// <summary>1, 2, 3... per question: each new suggestion is a new version.</summary>
    public int Version { get; set; }

    public bool IsCurrent { get; set; } = true;

    /// <summary>Two or three sentences a tenant can confirm at a glance. Null when the sources do not answer the question.</summary>
    public string? Summary { get; set; }

    /// <summary>The longer explanation, in plain text with short headings and bullets. Null when there is nothing more to say.</summary>
    public string? Detail { get; set; }

    public KnowledgeAnswerKind Kind { get; set; }

    public KnowledgeConfidence Confidence { get; set; }

    /// <summary>Kept separate from confidence: a High answer is still pending until the tenant acts.</summary>
    public KnowledgeVerificationStatus Status { get; set; } = KnowledgeVerificationStatus.PendingVerification;

    /// <summary>What the sources do not settle: an honest limitation, "no source states a price", an unproven number.</summary>
    public string? Limitation { get; set; }

    /// <summary>The one short question to ask the tenant to close the gap.</summary>
    public string? ClarifyingQuestion { get; set; }

    /// <summary>True for a price, guarantee, certification, compliance or outcome claim: it needs the tenant's explicit approval before customers hear it.</summary>
    public bool IsMaterialClaim { get; set; }

    /// <summary>True when the answer contains a figure (price, percentage, quantity) that no verified evidence contains.</summary>
    public bool HasUnsupportedClaim { get; set; }

    public bool HasConflict { get; set; }

    /// <summary>The provider that wrote it ("OpenAI", "Anthropic", "Google" or "Simulator") and its model.</summary>
    public string Provider { get; set; } = string.Empty;

    public string? Model { get; set; }

    /// <summary>True when the simulator produced it: placeholder text, never verified evidence, never publishable as fact.</summary>
    public bool IsSimulated { get; set; }

    public Guid? AiCorrelationId { get; set; }

    public DateTime GeneratedAt { get; set; }

    /// <summary>Why the application gave this confidence, one reason per line ("2 verified quotations from 1 passage; they cover 80% of the answer").</summary>
    public string? ConfidenceReasons { get; set; }

    /// <summary>The ids and versions of the sources the answer was drafted from, to flag it when one of them changes.</summary>
    public string? SourceVersions { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }
}

/// <summary>One verified quotation supporting an answer. The excerpt was checked against the passage's real text before it was stored.</summary>
public class AnswerEvidence : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid AnswerId { get; set; }

    public Guid SourceId { get; set; }

    public Guid PassageId { get; set; }

    public int SourceVersion { get; set; }

    /// <summary>The words quoted, as they appear in the source.</summary>
    public string Excerpt { get; set; } = string.Empty;

    /// <summary>The document name or website address, as it was when the answer was drafted.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>"Page 3", or the page address.</summary>
    public string Location { get; set; } = string.Empty;

    public string? Heading { get; set; }

    /// <summary>The passage's fingerprint then: if it changes, the evidence is stale.</summary>
    public string PassageHash { get; set; } = string.Empty;
}

/// <summary>Two or more sources giving different values for the same fact (a price, a duration). Both values and where each came from are kept,
/// and nothing decides between them but the tenant.</summary>
public class KnowledgeConflict : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid? AnswerId { get; set; }

    /// <summary>What the values are about ("price", "duration", "validity").</summary>
    public string Fact { get; set; } = string.Empty;

    /// <summary>JSON: each value with its source name, location and the sentence it came from.</summary>
    public string ValuesJson { get; set; } = "[]";

    public KnowledgeConflictStatus Status { get; set; } = KnowledgeConflictStatus.Open;

    public string? ChosenValue { get; set; }

    public Guid? ResolvedBy { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime DetectedAt { get; set; }
}

/// <summary>One feature of the product, the customer problem it addresses and the benefit it brings, each backed by verified quotations. A
/// feature the sources do not support is not listed; a benefit with a number no source gives is flagged, not accepted.</summary>
public class FeatureBenefit : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public int RunVersion { get; set; }

    public string Feature { get; set; } = string.Empty;

    public string? Problem { get; set; }

    public string? Benefit { get; set; }

    public string? Segment { get; set; }

    public KnowledgeConfidence Confidence { get; set; }

    public bool HasUnsupportedClaim { get; set; }

    /// <summary>JSON: the verified quotations (source name, location, excerpt, passage id).</summary>
    public string EvidenceJson { get; set; } = "[]";

    public string Provider { get; set; } = string.Empty;

    public bool IsSimulated { get; set; }

    public DateTime GeneratedAt { get; set; }
}

/// <summary>One "draft answers from my sources" run: it walks the questions in the background, so the tenant can watch progress and leave.</summary>
public class KnowledgeDraftRun : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public KnowledgeDraftRunStatus Status { get; set; } = KnowledgeDraftRunStatus.Queued;

    public int Total { get; set; }

    public int Done { get; set; }

    public int Drafted { get; set; }

    public int NoEvidence { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public string? Error { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
