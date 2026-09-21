using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// The hard metadata filter (§K.3) as a LINQ predicate, for the stores that run through EF Core -
/// the JSON vector store and the in-application keyword store. The raw-SQL stores use
/// <see cref="EligibleChunkQuery"/>; the two are the same list of conditions in the same order.
///
/// One LINQ definition rather than one per store, because the danger of this filter is not that it is
/// hard to write but that it is easy to write slightly differently in two places. The failure is
/// silent and one-directional: a copy that omits a clause simply returns more.
/// </summary>
internal static class EligibleChunkLinq
{
    /// <param name="matchEmbeddingSpace">True for the vector leg, which may only compare vectors from
    /// the query's own provider and model. False for the keyword leg, which does not use vectors at
    /// all and must not lose a chunk merely because it was embedded by a different provider.</param>
    public static IQueryable<KnowledgeBaseChunk> Eligible(
        this IQueryable<KnowledgeBaseChunk> chunks, RetrievalFilter filter, bool matchEmbeddingSpace) =>
        chunks.Where(c => c.IsActive
                          && c.ArticleStatus == KnowledgeArticleStatus.Published
                          && c.IsCurrentArticleVersion
                          // Tenant isolation, stated here as well as by the DbSet's ambient query
                          // filter: IVectorStore's contract is that the RetrievalFilter is applied by
                          // the store, so isolation cannot depend on whatever scope built it.
                          && (c.TenantId == null || c.TenantId == filter.TenantId)
                          && (!filter.GlobalOnly || c.TenantId == null)
                          && c.EffectiveFrom <= filter.NowUtc
                          && (c.EffectiveTo == null || c.EffectiveTo > filter.NowUtc)
                          && (c.CountryCode == null || c.CountryCode == filter.TenantCountryCode)
                          && (c.LanguageCode == filter.TicketLanguageCode || c.LanguageCode == "en")
                          && (filter.TenantVersionNumeric == null || c.VersionMinNumeric == null || filter.TenantVersionNumeric >= c.VersionMinNumeric)
                          && (filter.TenantVersionNumeric == null || c.VersionMaxNumeric == null || filter.TenantVersionNumeric <= c.VersionMaxNumeric)
                          && (filter.ProductModule == null || c.ProductModule == filter.ProductModule)
                          && (filter.ArticleId == null || c.ArticleId == filter.ArticleId)
                          && (!matchEmbeddingSpace || filter.EmbeddingProvider == null || c.EmbeddingProvider == filter.EmbeddingProvider)
                          && (!matchEmbeddingSpace || filter.EmbeddingModel == null || c.EmbeddingModel == filter.EmbeddingModel));
}
