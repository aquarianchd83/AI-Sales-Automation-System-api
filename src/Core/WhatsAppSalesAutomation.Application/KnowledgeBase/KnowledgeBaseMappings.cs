using System;
using System.Collections.Generic;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

public static class KnowledgeBaseMappings
{
    public static KnowledgeBaseArticleDto ToDto(
        this KnowledgeBaseArticle article,
        int chunkCount,
        IReadOnlyList<ArticleModelPublicationDto>? publishedModels = null,
        string? embeddingProvider = null,
        string? embeddingModel = null,
        IReadOnlyList<ArticleEmbeddingProviderDto>? embeddedProviders = null) => new(
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
        article.UpdatedAt,
        publishedModels ?? Array.Empty<ArticleModelPublicationDto>(),
        embeddingProvider,
        embeddingModel,
        embeddedProviders ?? Array.Empty<ArticleEmbeddingProviderDto>());

    public static ArticleModelPublicationDto ToDto(this KnowledgeBaseArticleModelPublication publication) =>
        new(publication.Provider.ToString(), publication.PublishedAt, publication.PublishedBy);
}
