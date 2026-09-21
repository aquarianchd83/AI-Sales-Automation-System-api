using Microsoft.Data.SqlClient;
using WhatsAppSalesAutomation.Application.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// The hard metadata filter of §K.3, as one SQL fragment shared by every leg of retrieval.
///
/// It lives in exactly one place on purpose. Both the vector leg and the keyword leg run over the
/// same candidate set, and the tenant-isolation clause is part of it. Two hand-written copies of this
/// predicate would be two chances to omit that clause - and the omission would not fail, it would
/// return extra rows. Everything that needs eligible chunks composes this CTE rather than restating
/// its conditions.
/// </summary>
internal static class EligibleChunkQuery
{
    /// <summary>The CTE body. Callers wrap it: <c>WITH EligibleChunks AS (...)</c> then select from
    /// it. Every condition here is a hard filter - a chunk excluded by any of them cannot become a
    /// candidate through any later stage, no matter how it scores.</summary>
    public const string Cte = @"
    SELECT c.Id, c.ArticleId, c.ChunkIndex, c.ContextHeader, c.ChunkText,
           c.AuthorityRank, c.ProductModule, c.SourceType, c.AtomicGroupId, c.TenantId
    FROM   KnowledgeBaseChunks c
    WHERE  c.IsActive = 1
      AND  c.ArticleStatus = 'Published'            -- Approved is NOT eligible. Only Published.
      AND  c.IsCurrentArticleVersion = 1
      -- TENANT ISOLATION. This clause is the reason this fragment is not duplicated anywhere.
      AND  (c.TenantId IS NULL OR c.TenantId = @TenantId)
      -- TIME WINDOW
      AND  c.EffectiveFrom <= @NowUtc
      AND  (c.EffectiveTo IS NULL OR c.EffectiveTo > @NowUtc)
      -- COUNTRY: chunks with no country apply everywhere. A tenant with no known country
      -- (@TenantCountry IS NULL) therefore sees only country-agnostic chunks, per EC-24.
      AND  (c.CountryCode IS NULL OR c.CountryCode = @TenantCountry)
      -- LANGUAGE: the ticket's language, with English always eligible as a fallback so a Hindi
      -- ticket is answered from the English corpus rather than from nothing.
      AND  (c.LanguageCode = @TicketLanguage OR c.LanguageCode = 'en')
      -- PLATFORM VERSION: skipped entirely when the tenant's version is unknown (EC-25) - an
      -- unknown version must not silently exclude every version-bounded article.
      AND  (@TenantVersion IS NULL OR c.VersionMinNumeric IS NULL OR @TenantVersion >= c.VersionMinNumeric)
      AND  (@TenantVersion IS NULL OR c.VersionMaxNumeric IS NULL OR @TenantVersion <= c.VersionMaxNumeric)
      -- Diagnostics only: both are NULL for ordinary retrieval, where module is a boost, not a filter.
      AND  (@ProductModule IS NULL OR c.ProductModule = @ProductModule)
      AND  (@ArticleId IS NULL OR c.ArticleId = @ArticleId)
      -- Same embedding space as the query, or the similarity is meaningless. See RetrievalFilter.
      AND  (@EmbeddingProvider IS NULL OR c.EmbeddingProvider = @EmbeddingProvider)
      AND  (@EmbeddingModel IS NULL OR c.EmbeddingModel = @EmbeddingModel)";

    /// <summary>Binds the fragment's parameters. Kept next to the SQL so a new condition and its
    /// parameter are added in one edit - a missing parameter is at least a loud failure, but a
    /// parameter bound in only one of two call sites would not be.</summary>
    public static SqlParameter[] Parameters(RetrievalFilter filter) => new[]
    {
        Nullable("@TenantId", filter.TenantId),
        new SqlParameter("@NowUtc", filter.NowUtc),
        Nullable("@TenantCountry", filter.TenantCountryCode),
        new SqlParameter("@TicketLanguage", filter.TicketLanguageCode),
        Nullable("@TenantVersion", filter.TenantVersionNumeric),
        Nullable("@ProductModule", filter.ProductModule?.ToString()),
        Nullable("@ArticleId", filter.ArticleId),
        Nullable("@EmbeddingProvider", filter.EmbeddingProvider),
        Nullable("@EmbeddingModel", filter.EmbeddingModel)
    };

    /// <summary>ADO.NET maps a CLR null to "no value supplied" rather than SQL NULL, which would make
    /// every <c>@X IS NULL</c> branch above throw instead of widening the filter.</summary>
    private static SqlParameter Nullable(string name, object? value) =>
        new(name, value ?? DBNull.Value);
}
