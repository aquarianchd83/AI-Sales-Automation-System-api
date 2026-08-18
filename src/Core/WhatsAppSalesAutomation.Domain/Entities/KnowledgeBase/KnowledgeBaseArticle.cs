using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

/// <summary>Canonical, human-authored/approved source text the AI is allowed to ground replies in.
/// Only <see cref="Status"/> == Published is ever chunked/embedded for retrieval - see
/// KnowledgeBaseArticleStatus's doc comment.</summary>
public class KnowledgeBaseArticle : BaseEntity, ISoftDelete
{
    public string Title { get; set; } = string.Empty;

    public string? Category { get; set; }

    public KnowledgeBaseSourceType SourceType { get; set; } = KnowledgeBaseSourceType.Manual;

    public string Content { get; set; } = string.Empty;

    public KnowledgeBaseArticleStatus Status { get; set; } = KnowledgeBaseArticleStatus.Draft;

    /// <summary>Incremented on every edit that changes Content - lets KnowledgeBaseChunks record which
    /// version they were embedded from, so a stale re-embed after an edit is detectable.</summary>
    public int Version { get; set; } = 1;

    public Guid? ApprovedBy { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public ICollection<KnowledgeBaseChunk> Chunks { get; set; } = new List<KnowledgeBaseChunk>();
}
