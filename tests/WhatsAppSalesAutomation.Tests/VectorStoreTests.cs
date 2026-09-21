using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The fallback vector store and the two pure helpers around it.
///
/// The searches here are all about the HARD FILTER rather than about ranking quality. A vector store
/// that ranks poorly gives a worse answer; one that filters poorly gives another tenant's answer, or
/// an answer from a policy that expired last month. Only the second kind is silent.
/// </summary>
public sealed class VectorStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly JsonColumnVectorStore _store;

    public VectorStoreTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        // TenantA's own context, because that is how retrieval actually runs: in the tenant's DI
        // scope, with the ambient query filter and the RetrievalFilter agreeing. A platform context
        // would make the ambient filter STRICTER than the one under test (a SuperAdmin sees GLOBAL
        // only), so every tenant-scoped chunk would vanish before the store's own clause was
        // reached - which proves nothing about the store.
        //
        // The store's explicit clause is covered separately by
        // The_filters_tenant_is_honoured_even_within_one_tenants_context below.
        _db = new SqliteApplicationDbContext(options, new StubTenant(TenantA), new AnonymousUser());
        _db.Database.EnsureCreated();
        _store = new JsonColumnVectorStore(_db);
    }

    // ── SemanticVersion ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2.10.0", 2_010_000L)]
    [InlineData("2.9.0", 2_009_000L)]
    [InlineData("v2.10.3", 2_010_003L)]
    [InlineData("2.1", 2_001_000L)]
    [InlineData("3", 3_000_000L)]
    [InlineData("2.1.0-rc.1", 2_001_000L)]
    [InlineData("2.1.0+build.5", 2_001_000L)]
    public void Versions_normalize_to_sortable_numbers(string input, long expected)
    {
        Assert.Equal(expected, SemanticVersion.ToNumeric(input));
    }

    [Fact]
    public void The_case_string_comparison_gets_wrong()
    {
        // This single assertion is the entire reason the numeric column exists: as text, "2.10.0"
        // sorts below "2.9.0", so a version-bounded article would be filtered out of exactly the
        // releases its bound was written to cover.
        Assert.True(string.CompareOrdinal("2.10.0", "2.9.0") < 0);
        Assert.True(SemanticVersion.ToNumeric("2.10.0") > SemanticVersion.ToNumeric("2.9.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4")]
    [InlineData("-1.0.0")]
    public void Unparseable_versions_are_unbounded_rather_than_zero(string? input)
    {
        // Null means "no bound", which widens applicability. Returning 0 would instead mean "version
        // zero", quietly excluding the article from every real release - a typo in a metadata field
        // silently deleting an article from retrieval.
        Assert.Null(SemanticVersion.ToNumeric(input));
    }

    // ── Vector literal formatting ────────────────────────────────────────────────────────

    [Fact]
    public void Vector_literal_is_json_and_round_trips()
    {
        var literal = SqlServerVectorStore.ToVectorLiteral(new[] { 0.5f, -0.25f, 0f });

        Assert.Equal("[0.5,-0.25,0]", literal);
        Assert.Equal(new[] { 0.5f, -0.25f, 0f }, System.Text.Json.JsonSerializer.Deserialize<float[]>(literal));
    }

    [Fact]
    public void Vector_literal_ignores_the_current_culture()
    {
        // Under a comma-decimal culture the default float formatting yields "0,5", turning a
        // 2-element vector into a 4-element JSON array of the wrong numbers - which SQL Server would
        // accept as a perfectly valid vector of the wrong length or, worse, the right length.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("[0.5,-0.25]", SqlServerVectorStore.ToVectorLiteral(new[] { 0.5f, -0.25f }));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── Cosine ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cosine_handles_the_degenerate_inputs_without_throwing()
    {
        // A single chunk embedded under a different model has a different dimensionality. It should
        // drop out of the ranking, not take the whole query down.
        Assert.Equal(0, JsonColumnVectorStore.CosineSimilarity(new[] { 1f, 0f }, new[] { 1f, 0f, 0f }));
        Assert.Equal(0, JsonColumnVectorStore.CosineSimilarity(Array.Empty<float>(), new[] { 1f }));
        Assert.Equal(0, JsonColumnVectorStore.CosineSimilarity(new[] { 0f, 0f }, new[] { 1f, 0f }));
        Assert.Equal(1, JsonColumnVectorStore.CosineSimilarity(new[] { 1f, 0f }, new[] { 1f, 0f }), 6);
    }

    // ── The hard filter ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_this_tenants_chunks_and_global_ones()
    {
        Seed("mine", TenantA);
        Seed("global", null);
        Seed("theirs", TenantB);

        var ids = await SearchKeysAsync(Filter());

        Assert.Equal(new[] { "global", "mine" }, ids);
    }

    [Fact]
    public async Task Excludes_chunks_from_an_article_that_is_not_published()
    {
        Seed("published", TenantA);
        Seed("approved", TenantA, status: KnowledgeArticleStatus.Approved);
        Seed("draft", TenantA, status: KnowledgeArticleStatus.Draft);

        // Approved is chunked and embedded so that publishing is a flag flip rather than a re-index -
        // which is exactly why it has to be excluded HERE instead of by never being indexed.
        Assert.Equal(new[] { "published" }, await SearchKeysAsync(Filter()));
    }

    [Fact]
    public async Task Excludes_superseded_and_deactivated_chunks()
    {
        Seed("current", TenantA);
        Seed("old-version", TenantA, isCurrentArticleVersion: false);
        Seed("mid-reindex", TenantA, isActive: false);

        Assert.Equal(new[] { "current" }, await SearchKeysAsync(Filter()));
    }

    [Fact]
    public async Task Respects_the_effective_window()
    {
        Seed("live", TenantA);
        Seed("not-yet", TenantA, effectiveFrom: Now.AddDays(10));
        Seed("expired", TenantA, effectiveTo: Now.AddDays(-1));

        // A policy that expired yesterday is invisible immediately, without waiting for the job that
        // moves it to Deprecated - the query-time check is what makes that true.
        Assert.Equal(new[] { "live" }, await SearchKeysAsync(Filter()));
    }

    [Fact]
    public async Task Country_specific_chunks_need_a_matching_tenant_country()
    {
        Seed("anywhere", TenantA);
        Seed("india", TenantA, countryCode: "IN");
        Seed("uae", TenantA, countryCode: "AE");

        Assert.Equal(new[] { "anywhere", "india" }, await SearchKeysAsync(Filter() with { TenantCountryCode = "IN" }));
    }

    [Fact]
    public async Task A_tenant_with_no_known_country_sees_only_country_agnostic_chunks()
    {
        Seed("anywhere", TenantA);
        Seed("india", TenantA, countryCode: "IN");

        // EC-24. The alternative - treating unknown as "matches everything" - would answer a tenant
        // with another country's refund policy.
        Assert.Equal(new[] { "anywhere" }, await SearchKeysAsync(Filter() with { TenantCountryCode = null }));
    }

    [Fact]
    public async Task English_stays_eligible_alongside_the_ticket_language()
    {
        Seed("english", TenantA, languageCode: "en");
        Seed("hindi", TenantA, languageCode: "hi");
        Seed("french", TenantA, languageCode: "fr");

        Assert.Equal(new[] { "english", "hindi" }, await SearchKeysAsync(Filter() with { TicketLanguageCode = "hi" }));
    }

    [Fact]
    public async Task Version_bounds_are_applied_numerically()
    {
        Seed("unbounded", TenantA);
        Seed("from-2.9", TenantA, versionMin: SemanticVersion.ToNumeric("2.9.0"));
        Seed("upto-2.9", TenantA, versionMax: SemanticVersion.ToNumeric("2.9.0"));

        // A tenant on 2.10.0 is PAST 2.9.0. Under a string comparison it would have been judged
        // earlier, getting the wrong article in both directions at once.
        var onTwoTen = Filter() with { TenantVersionNumeric = SemanticVersion.ToNumeric("2.10.0") };

        Assert.Equal(new[] { "from-2.9", "unbounded" }, await SearchKeysAsync(onTwoTen));
    }

    [Fact]
    public async Task An_unknown_tenant_version_skips_the_bounds_entirely()
    {
        Seed("unbounded", TenantA);
        Seed("from-2.9", TenantA, versionMin: SemanticVersion.ToNumeric("2.9.0"));
        Seed("upto-2.9", TenantA, versionMax: SemanticVersion.ToNumeric("2.9.0"));

        // EC-25: an unknown version must not exclude every version-bounded article.
        Assert.Equal(
            new[] { "from-2.9", "unbounded", "upto-2.9" },
            await SearchKeysAsync(Filter() with { TenantVersionNumeric = null }));
    }

    [Fact]
    public async Task Unembedded_chunks_are_not_candidates()
    {
        Seed("embedded", TenantA);
        Seed("awaiting-embedding", TenantA, embedded: false);

        Assert.Equal(new[] { "embedded" }, await SearchKeysAsync(Filter()));
    }

    [Fact]
    public async Task Ranks_by_similarity_and_honours_topN()
    {
        Seed("exact", TenantA, embedding: new[] { 1f, 0f });
        Seed("orthogonal", TenantA, embedding: new[] { 0f, 1f });
        Seed("opposite", TenantA, embedding: new[] { -1f, 0f });

        var hits = await _store.SearchAsync(new[] { 1f, 0f }, Filter(), topN: 2);

        Assert.Equal(2, hits.Count);
        Assert.Equal("exact", KeyOf(hits[0].ChunkId));
        Assert.Equal("orthogonal", KeyOf(hits[1].ChunkId));
        Assert.True(hits[0].Similarity > hits[1].Similarity);
    }

    [Fact]
    public async Task An_empty_query_vector_returns_nothing_rather_than_everything()
    {
        Seed("something", TenantA);

        // The embedding provider returns an empty array when its call fails. Scoring everything at
        // zero and returning the first N would hand the agent arbitrary chunks that look retrieved.
        Assert.Empty(await _store.SearchAsync(ReadOnlyMemory<float>.Empty, Filter(), topN: 5));
    }

    [Fact]
    public async Task The_filters_tenant_is_honoured_even_within_one_tenants_context()
    {
        Seed("mine", TenantA);
        Seed("global", null);

        // The ambient context is TenantA, so EF's own filter would allow "mine" through. Asking for
        // TenantB must still exclude it - this is what proves the store applies the RetrievalFilter
        // itself rather than inheriting isolation from whatever scope it happened to be built in.
        var asOtherTenant = await SearchKeysAsync(Filter() with { TenantId = TenantB });

        Assert.Equal(new[] { "global" }, asOtherTenant);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static RetrievalFilter Filter() => new()
    {
        TenantId = TenantA,
        NowUtc = Now,
        TicketLanguageCode = "en",
        TenantCountryCode = "IN",
        TenantVersionNumeric = SemanticVersion.ToNumeric("2.10.0")
    };

    private async Task<string[]> SearchKeysAsync(RetrievalFilter filter)
    {
        var hits = await _store.SearchAsync(new[] { 1f, 0f }, filter, topN: 50);
        return hits.Select(h => KeyOf(h.ChunkId)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private string KeyOf(Guid chunkId) => _db.KnowledgeBaseChunks.AsNoTracking().Single(c => c.Id == chunkId).ChunkText;

    private void Seed(
        string key,
        Guid? tenantId,
        KnowledgeArticleStatus status = KnowledgeArticleStatus.Published,
        bool isActive = true,
        bool isCurrentArticleVersion = true,
        DateTime? effectiveFrom = null,
        DateTime? effectiveTo = null,
        string? countryCode = null,
        string languageCode = "en",
        long? versionMin = null,
        long? versionMax = null,
        float[]? embedding = null,
        bool embedded = true)
    {
        // The chunk's article has to exist for the foreign key, but nothing here reads it: the
        // retrieval filter runs entirely off the chunk's denormalized copies, which is the point of
        // denormalizing them. Its fields are set consistently anyway so the row is not misleading to
        // anyone debugging a failure here.
        var article = new KnowledgeBaseArticle
        {
            TenantId = tenantId,
            ArticleKey = key,
            Title = key,
            Content = key,
            ContentHash = key,
            Status = status,
            IsCurrentVersion = isCurrentArticleVersion,
            LanguageCode = languageCode,
            CountryCode = countryCode,
            EffectiveFrom = effectiveFrom ?? Now.AddDays(-30),
            EffectiveTo = effectiveTo,
            SourceType = KnowledgeSourceType.AdminConfiguredArticle,
            AuthorityRank = 30,
            ApprovedBy = Guid.NewGuid(),
            ApprovedAt = Now.AddDays(-30)
        };
        _db.KnowledgeBaseArticles.Add(article);

        // ChunkText doubles as the chunk's name in the assertions - a readable failure message
        // matters more here than realistic prose.
        _db.KnowledgeBaseChunks.Add(new KnowledgeBaseChunk
        {
            TenantId = tenantId,
            ArticleId = article.Id,
            ChunkIndex = 0,
            ChunkText = key,
            ContextHeader = string.Empty,
            EmbeddingInput = key,
            SearchText = key,
            Embedding = embedded
                ? SqlServerVectorStore.ToVectorLiteral(embedding ?? new[] { 1f, 0f })
                : null,
            ArticleStatus = status,
            IsActive = isActive,
            IsCurrentArticleVersion = isCurrentArticleVersion,
            EffectiveFrom = effectiveFrom ?? Now.AddDays(-30),
            EffectiveTo = effectiveTo,
            CountryCode = countryCode,
            LanguageCode = languageCode,
            VersionMinNumeric = versionMin,
            VersionMaxNumeric = versionMax,
            SourceType = KnowledgeSourceType.AdminConfiguredArticle,
            AuthorityRank = 30
        });

        _db.SaveChanges();
    }

    private sealed class StubTenant : ITenantContext
    {
        public StubTenant(Guid? tenantId) => TenantId = tenantId;

        public Guid? TenantId { get; private set; }

        public bool IsPlatformSuperAdmin => false;

        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
