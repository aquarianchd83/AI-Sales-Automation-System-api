using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// One change to one answer of a tenant's questionnaire: what it was and what it became. Together they are the audit trail of
/// "who told the studio what, and when". Append-only; the live answers stay on <see cref="QuestionnaireResponse"/>.
/// A null <see cref="NewJson"/> means the answer was removed (cleared by the tenant, or dropped because its question stopped being asked).
/// </summary>
public class QuestionnaireAnswerRevision : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ResponseId { get; set; }

    public string QuestionKey { get; set; } = string.Empty;

    public string? PreviousJson { get; set; }

    public string? NewJson { get; set; }

    /// <summary>Set, Changed, Cleared or Hidden (the question is no longer asked because an earlier answer changed).</summary>
    public string Kind { get; set; } = string.Empty;

    public Guid? ChangedBy { get; set; }
}

/// <summary>
/// One entry in a business fact's history: the fact as it stood after a change, and why it changed. Append-only, so "this was verified
/// by X on that date, then the answer changed and it went back to Draft" can always be read back.
/// </summary>
public class BusinessFactRevision : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid FactId { get; set; }

    /// <summary>1, 2, 3... per fact.</summary>
    public int Number { get; set; }

    public BusinessFactChangeKind Kind { get; set; }

    public string Category { get; set; } = string.Empty;

    public string? Subject { get; set; }

    public string Statement { get; set; } = string.Empty;

    public string? Context { get; set; }

    public BusinessFactStatus Status { get; set; }

    /// <summary>Why, in a sentence a tenant can read ("You changed your answer to 'Opening hours'").</summary>
    public string? Note { get; set; }

    public Guid? ChangedBy { get; set; }
}

/// <summary>
/// A tenant's decision to skip or postpone a gap the readiness check found, keyed by the requirement's key. One row per tenant and
/// requirement; filling the gap or reopening it removes the decision.
/// </summary>
public class KnowledgeGapDecision : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string RequirementKey { get; set; } = string.Empty;

    public KnowledgeGapDecisionKind Kind { get; set; }

    /// <summary>For a deferral, when the gap comes back.</summary>
    public DateTime? DeferUntilUtc { get; set; }

    public Guid? DecidedBy { get; set; }
}
