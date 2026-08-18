using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

public static class KnowledgeBaseMappings
{
    public static KnowledgeBaseArticleDto ToDto(this KnowledgeBaseArticle article, int chunkCount) => new(
        article.Id,
        article.Title,
        article.Category,
        article.SourceType.ToString(),
        article.Content,
        article.Status.ToString(),
        article.Version,
        article.ApprovedBy,
        chunkCount,
        article.CreatedAt,
        article.UpdatedAt);
}
