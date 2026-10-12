using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// A questionnaire the platform offers tenants, so the studio learns about a business by asking rather than guessing. Platform-owned and
/// deliberately NOT <see cref="ITenantOwned"/>: every tenant reads the same published template, and only a PlatformSuperAdmin edits it.
/// An <see cref="Industry"/> of null is the generic template; a tenant gets the one matching its own industry, else the generic one.
/// Industry knowledge lives here, as data - never in code.
/// </summary>
public class QuestionnaireTemplate : BaseEntity
{
    /// <summary>Stable machine name, unique, e.g. "generic-business" or "eye-care".</summary>
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Matched case-insensitively against <c>Tenant.Industry</c>. Null = applies to any industry.</summary>
    public string? Industry { get; set; }

    /// <summary>False stops the template being offered to tenants; its history stays.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One immutable-once-published version of a template's questions. The questions, answer types, conditions and fact rules are one JSON
/// document (see <c>QuestionnaireDefinition</c>) rather than a table per concept: a version is always read and published whole, and
/// keeping it whole is what makes "this response was answered against exactly this" true.
/// </summary>
public class QuestionnaireTemplateVersion : BaseEntity
{
    public Guid TemplateId { get; set; }

    /// <summary>1, 2, 3... per template.</summary>
    public int Number { get; set; }

    public QuestionnaireVersionStatus Status { get; set; } = QuestionnaireVersionStatus.Draft;

    public string DefinitionJson { get; set; } = "{}";

    public DateTime? PublishedAtUtc { get; set; }

    public Guid? PublishedBy { get; set; }
}

/// <summary>
/// One tenant's answers to one version of a template. Pinned to that version, so publishing a newer one never changes what an earlier
/// answer means. The answers are one JSON object keyed by question key; an answer to a question that is currently hidden is never kept.
/// </summary>
public class QuestionnaireResponse : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid TemplateId { get; set; }

    public Guid VersionId { get; set; }

    public QuestionnaireResponseStatus Status { get; set; } = QuestionnaireResponseStatus.InProgress;

    public string AnswersJson { get; set; } = "{}";

    public DateTime? CompletedAtUtc { get; set; }

    public Guid? CompletedBy { get; set; }
}

/// <summary>
/// One thing the tenant says is true about its business ("We are open Monday to Saturday, 9 to 6"). Scripts may only assert Verified
/// facts, so a person has to confirm each one first. A fact the questionnaire produced is tied to its question by
/// <see cref="SourceRef"/>; re-answering the question updates the same fact, and a verified fact whose wording changes goes back to Draft.
/// </summary>
public class BusinessFact : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Free grouping label, e.g. "About", "Services", "Contact".</summary>
    public string Category { get; set; } = string.Empty;

    public string Statement { get; set; } = string.Empty;

    public BusinessFactSource SourceType { get; set; } = BusinessFactSource.Manual;

    /// <summary>For a questionnaire fact, the question key it came from. Null for a manual one.</summary>
    public string? SourceRef { get; set; }

    public BusinessFactStatus Status { get; set; } = BusinessFactStatus.Draft;

    public Guid? VerifiedBy { get; set; }

    public DateTime? VerifiedAtUtc { get; set; }

    public Guid? UpdatedBy { get; set; }
}
