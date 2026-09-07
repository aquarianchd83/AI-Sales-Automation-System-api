using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

/// <summary>Records that a KnowledgeBaseArticle has been explicitly made eligible for a specific
/// AI chat model - e.g. an article published to OpenAI only will not be retrieved when Anthropic
/// is the active chat provider, even though both share the same embedded KnowledgeBaseChunks (see
/// KnowledgeBaseService.RetrieveRelevantChunksAsync's provider filter). One row per
/// (ArticleId, Provider) pair - KnowledgeBaseArticleModelPublicationConfiguration enforces that
/// with a unique index. Independent of KnowledgeBaseArticle.Status: Status still just means "has
/// embedded chunks"; this is an additional eligibility layer on top, not a replacement for it.
/// </summary>
public class KnowledgeBaseArticleModelPublication : BaseEntity
{
    public Guid ArticleId { get; set; }

    public AiModelProvider Provider { get; set; }

    public DateTime PublishedAt { get; set; }

    /// <summary>Who published the article to this model. Nullable for parity with
    /// LeadActivity.CreatedBy, though in practice every publish goes through an authenticated
    /// controller action, so this is expected to always be set - see KnowledgeBaseController.</summary>
    public Guid? PublishedBy { get; set; }
}
