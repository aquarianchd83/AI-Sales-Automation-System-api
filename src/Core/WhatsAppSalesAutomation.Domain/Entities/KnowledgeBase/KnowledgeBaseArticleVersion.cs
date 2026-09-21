using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

/// <summary>
/// An immutable snapshot taken at each publish.
///
/// The live <see cref="KnowledgeBaseArticle"/> row keeps changing - status, review dates, lifecycle
/// notes, and eventually its content. This does not. It is what an audit reads when it asks "what
/// exactly did this article say on the day the AI cited it", a question the live row cannot answer
/// because by then it has been edited.
///
/// Append-only: nothing updates or deletes a row here. The article's own lifecycle (deprecate,
/// rollback, archive) is recorded on the live row, not by rewriting history.
/// </summary>
public class KnowledgeBaseArticleVersion : BaseEntity, ITenantScopedOrGlobal
{
    /// <summary>Copied from the article at publish time. NULL = GLOBAL, same meaning as on the
    /// article - and the same query filter, so a tenant can read the history of platform knowledge
    /// but never another tenant's.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>The live row this was snapshotted from. Not a foreign key with a cascade: a hard
    /// delete of the article must not take its history with it, which is the one moment the history
    /// is most likely to be wanted.</summary>
    public Guid ArticleId { get; set; }

    /// <summary>Denormalized so history survives even if the article row is gone, and so the whole
    /// lineage of an ArticleKey can be listed without joining.</summary>
    public string ArticleKey { get; set; } = string.Empty;

    public int VersionNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The entire metadata set as it was at publish time, as JSON. A column-per-field copy
    /// would need a migration every time the article schema grows, and this table is append-only
    /// history that nothing queries by field - it is read whole, by version.</summary>
    public string MetadataJson { get; set; } = string.Empty;

    public Guid PublishedBy { get; set; }

    public DateTime PublishedAt { get; set; }

    public Guid ApprovedBy { get; set; }

    public DateTime ApprovedAt { get; set; }

    /// <summary>Why this version was created. Free text from the publisher, e.g. "GST rate change
    /// effective Oct 1".</summary>
    public string? ChangeNote { get; set; }
}
