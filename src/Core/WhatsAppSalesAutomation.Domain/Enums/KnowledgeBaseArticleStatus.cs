namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Only <see cref="Published"/> articles are chunked/embedded and eligible for RAG retrieval -
/// see <c>KnowledgeBaseService.PublishAsync</c>. Draft lets an article be written and reviewed without
/// it being usable by the AI yet; Archived removes it from retrieval without deleting the history.</summary>
public enum KnowledgeBaseArticleStatus
{
    Draft = 0,
    Published = 1,
    Archived = 2
}
