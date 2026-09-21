using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The whole retrieval pipeline over real SQLite and the real stores, with only the embedding
/// provider and the reranker faked.
///
/// The tests that matter most are about what does NOT come back: another tenant's chunk, an unpublished
/// one, one from a different embedding space. Retrieval that finds the right answer is expected; the
/// failure worth catching is retrieval that also finds a wrong one and ranks it confidently.
/// </summary>
public sealed class KnowledgeRetrievalServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    // Axes: refund, credit, everything else. Enough to make vector similarity legible in a test.
    private static readonly float[] RefundVector = { 1f, 0f, 0f };
    private static readonly float[] CreditVector = { 0f, 1f, 0f };
    private static readonly float[] OtherVector = { 0f, 0f, 1f };

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext _seedDb;
    private readonly DictionaryCache _cache = new();
    private readonly FakeReranker _reranker = new();

    public KnowledgeRetrievalServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _seedDb = new SqliteApplicationDbContext(options, new StubTenant(null, superAdmin: true), new AnonymousUser());
        _seedDb.Database.EnsureCreated();
    }

    private (KnowledgeRetrievalService Service, FakeEmbedder Embedder) NewService(
        Guid? tenantId, FakeEmbedder? embedder = null, SupportRagOptions? options = null, IReranker? reranker = null)
    {
        var db = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options,
            new StubTenant(tenantId), new AnonymousUser());

        embedder ??= new FakeEmbedder();
        var service = new KnowledgeRetrievalService(
            db, embedder, new JsonColumnVectorStore(db), new Bm25KeywordStore(db), reranker ?? _reranker, _cache,
            new StubTenant(tenantId), new HeuristicTokenCounter(), new TestClock { UtcNow = Now },
            Options.Create(options ?? new SupportRagOptions()), NullLogger<KnowledgeRetrievalService>.Instance);

        return (service, embedder);
    }

    private static KnowledgeRetrievalRequest Ask(string query, params string[] context) =>
        new(query, context, null, null, "IN", "en", "2.10.0");

    private static SupportRagOptions Lenient() => new();

    // ── Finding the right thing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_relevant_chunk_is_found_and_carries_what_a_citation_needs()
    {
        Seed("refund-policy", "Refunds take seven days.", RefundVector, "Refund policy");
        Seed("credit-guide", "Credits are bought in packs.", CreditVector, "Credit guide");
        _reranker.ScoreBy = text => text.Contains("Refunds") ? 0.9 : 0.1;

        var result = (await NewService(TenantA).Service.RetrieveAsync(Ask("refund"))).Evidence;

        var top = result[0];
        Assert.Equal("refund-policy", top.ArticleKey);
        Assert.Equal("Refund policy", top.Title);
        Assert.Equal(1, top.ArticleVersionNumber);
        Assert.Equal(1, top.Rank);
        Assert.True(top.VectorScore > 0.99);
        Assert.True(top.FusedScore > 0);
    }

    [Fact]
    public async Task The_keyword_leg_finds_an_exact_token_that_the_vector_leg_cannot_tell_apart()
    {
        // Identical vectors: the embedding model sees these two chunks as the same thing, exactly as
        // it sees error 131047 and 131026 as the same thing. Only the keyword leg can separate them.
        Seed("err-131047", "Error 131047 means the re-engagement window has closed.", OtherVector, "Error 131047");
        Seed("err-131026", "Error 131026 means the message is undeliverable.", OtherVector, "Error 131026");

        var result = (await NewService(TenantA).Service.RetrieveAsync(Ask("what does 131047 mean"))).Evidence;

        Assert.Equal("err-131047", result[0].ArticleKey);
        Assert.True(result[0].KeywordScore > 0);
    }

    [Fact]
    public async Task A_global_article_is_retrieved_alongside_the_tenants_own()
    {
        Seed("platform-policy", "Platform refund policy.", RefundVector, "Policy", global: true);
        Seed("my-note", "My own refund note.", RefundVector, "Note", tenantId: TenantA);

        var keys = (await NewService(TenantA).Service.RetrieveAsync(Ask("refund"))).Evidence.Select(e => e.ArticleKey).ToList();

        Assert.Contains("platform-policy", keys);
        Assert.Contains("my-note", keys);
    }

    // ── What must NOT come back ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_tenants_private_chunk_is_never_returned()
    {
        Seed("mine", "Refund note.", RefundVector, "Mine", tenantId: TenantA);
        Seed("theirs", "Refund note, but it belongs to someone else.", RefundVector, "Theirs", tenantId: TenantB);

        var evidence = (await NewService(TenantA).Service.RetrieveAsync(Ask("refund note"))).Evidence;

        Assert.DoesNotContain(evidence, e => e.ArticleKey == "theirs");
    }

    [Fact]
    public async Task Unpublished_and_superseded_chunks_are_never_returned()
    {
        Seed("live", "Refund text live.", RefundVector, "Live");
        Seed("approved", "Refund text approved.", RefundVector, "Approved", status: KnowledgeArticleStatus.Approved);
        Seed("draft", "Refund text draft.", RefundVector, "Draft", status: KnowledgeArticleStatus.Draft);
        Seed("old", "Refund text superseded.", RefundVector, "Old", isCurrent: false);

        var keys = (await NewService(TenantA).Service.RetrieveAsync(Ask("refund text"))).Evidence.Select(e => e.ArticleKey).ToList();

        Assert.Equal(new[] { "live" }, keys);
    }

    [Fact]
    public async Task A_chunk_embedded_by_a_different_model_is_not_compared_by_vector()
    {
        // Same words in the query and the chunk, so the KEYWORD leg would find it - which is why the
        // query below shares no words with it. The point is the vector leg: comparing vectors from two
        // models is not weak evidence, it is noise that ranks confidently.
        Seed("other-space", "zzz unrelated words", RefundVector, "Other", provider: "Google", model: "text-embedding-004");

        var result = await NewService(TenantA).Service.RetrieveAsync(Ask("refund"));

        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task The_keyword_leg_still_finds_a_chunk_from_a_different_embedding_space()
    {
        // The other half of the same rule: the keyword leg does not use vectors, so it must not lose a
        // chunk merely because a different provider embedded it.
        Seed("other-space", "refund words here", RefundVector, "Other", provider: "Google", model: "text-embedding-004");

        var evidence = (await NewService(TenantA).Service.RetrieveAsync(Ask("refund words"))).Evidence;

        Assert.Contains(evidence, e => e.ArticleKey == "other-space");
    }

    [Fact]
    public async Task Country_specific_content_is_withheld_from_a_tenant_in_another_country()
    {
        Seed("india-only", "Refund rules for India.", RefundVector, "India", countryCode: "IN");
        Seed("uae-only", "Refund rules for the UAE.", RefundVector, "UAE", countryCode: "AE");

        var keys = (await NewService(TenantA).Service.RetrieveAsync(Ask("refund rules"))).Evidence.Select(e => e.ArticleKey).ToList();

        Assert.Contains("india-only", keys);
        Assert.DoesNotContain("uae-only", keys);
    }

    // ── Ranking ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_reranker_can_overturn_the_fusion_order()
    {
        Seed("close-but-wrong", "Refund refund refund keywords.", RefundVector, "Wrong");
        Seed("actual-answer", "You can buy more credits from the wallet.", CreditVector, "Answer");
        _reranker.ScoreBy = text => text.Contains("wallet") ? 0.95 : 0.10;

        var result = await NewService(TenantA).Service.RetrieveAsync(Ask("refund"));

        Assert.Equal("actual-answer", result.Evidence[0].ArticleKey);
        Assert.Equal(RetrievalMode.Reranked, result.Diagnostics.Mode);
    }

    [Fact]
    public async Task A_matching_module_outranks_an_otherwise_identical_chunk()
    {
        Seed("billing-one", "Refund details.", RefundVector, "Billing", module: ProductModule.Billing);
        Seed("campaign-one", "Refund details.", RefundVector, "Campaign", module: ProductModule.Campaigns);

        // No reranker, so ordering is fusion plus boosts only - which is where the module boost lives.
        var request = Ask("refund details") with { DetectedModule = ProductModule.Billing };
        var evidence = (await NewService(TenantA, reranker: new UnavailableReranker()).Service.RetrieveAsync(request)).Evidence;

        Assert.Equal("billing-one", evidence[0].ArticleKey);
    }

    [Fact]
    public async Task Higher_authority_wins_between_equally_relevant_chunks()
    {
        Seed("tenant-note", "Refund details.", RefundVector, "Note", tenantId: TenantA, authority: 30);
        Seed("platform-policy", "Refund details.", RefundVector, "Policy", global: true, authority: 90);

        var evidence = (await NewService(TenantA, reranker: new UnavailableReranker()).Service.RetrieveAsync(Ask("refund details"))).Evidence;

        Assert.Equal("platform-policy", evidence[0].ArticleKey);
    }

    // ── The gate, end to end ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Strong_corroborated_evidence_passes_the_gate()
    {
        Seed("a", "Refund policy part one.", RefundVector, "A");
        Seed("b", "Refund policy part two.", RefundVector, "B");
        _reranker.ScoreBy = _ => 0.9;

        var diagnostics = (await NewService(TenantA).Service.RetrieveAsync(Ask("refund policy"))).Diagnostics;

        Assert.True(diagnostics.EvidenceGatePassed, diagnostics.GateFailureReason);
        Assert.Equal(2, diagnostics.SupportingChunkCount);
    }

    [Fact]
    public async Task Weak_evidence_fails_the_gate_and_says_why()
    {
        Seed("a", "Refund policy.", RefundVector, "A");
        _reranker.ScoreBy = _ => 0.30;

        var diagnostics = (await NewService(TenantA).Service.RetrieveAsync(Ask("refund policy"))).Diagnostics;

        Assert.False(diagnostics.EvidenceGatePassed);
        Assert.Equal("TopScoreBelowThreshold", diagnostics.GateFailureReason);
    }

    [Fact]
    public async Task Nothing_found_fails_the_gate_rather_than_returning_the_least_bad_chunk()
    {
        Seed("unrelated", "Completely different topic.", OtherVector, "Unrelated", provider: "Google", model: "x");

        var result = await NewService(TenantA).Service.RetrieveAsync(Ask("refund"));

        Assert.Empty(result.Evidence);
        Assert.False(result.Diagnostics.EvidenceGatePassed);
        Assert.Equal("NoEvidence", result.Diagnostics.GateFailureReason);
    }

    [Fact]
    public async Task With_no_reranker_the_mode_is_recorded_and_the_gate_is_stricter()
    {
        Seed("a", "Refund policy one.", RefundVector, "A");
        Seed("b", "Refund policy two.", RefundVector, "B");

        var diagnostics = (await NewService(TenantA, reranker: new UnavailableReranker()).Service.RetrieveAsync(Ask("refund policy"))).Diagnostics;

        Assert.Equal(RetrievalMode.FusionOnly, diagnostics.Mode);
        // Two chunks would satisfy the reranked gate; fusion-only needs three.
        Assert.False(diagnostics.EvidenceGatePassed);
    }

    // ── Atomic groups ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Any_chunk_of_an_atomic_group_brings_in_the_whole_group_in_order()
    {
        var group = Guid.NewGuid();
        Seed("procedure", "Step one open billing wallet.", RefundVector, "Procedure", group: (group, 1, 3));
        Seed("procedure", "Step two choose invoice.", OtherVector, "Procedure", group: (group, 2, 3), sameArticleAsPrevious: true);
        Seed("procedure", "Step three press request.", OtherVector, "Procedure", group: (group, 3, 3), sameArticleAsPrevious: true);
        _reranker.ScoreBy = text => text.Contains("Step one") ? 0.9 : 0.01;

        // RerankTopN of 1 so that only step one is SELECTED on score. With the default of 8 all three
        // steps are selected anyway (the vector leg returns every eligible chunk), and the group rule
        // would have nothing to do - which is a test that passes without testing the rule.
        var evidence = (await NewService(TenantA, options: new SupportRagOptions { RerankTopN = 1 })
            .Service.RetrieveAsync(Ask("refund wallet"))).Evidence;

        var members = evidence.Where(e => e.AtomicGroupId == group).ToList();
        // Only step one matched, and steps two and three came with it: half a procedure is the
        // failure this rule exists to make impossible.
        Assert.Equal(3, members.Count);
        Assert.Equal(2, members.Count(m => m.IsGroupExpansion));
        Assert.Contains("Step one", members[0].ChunkText);
        Assert.Contains("Step two", members[1].ChunkText);
        Assert.Contains("Step three", members[2].ChunkText);
        // Included by rule, not earned by score - so it must not look like a strong hit.
        Assert.All(members.Where(m => m.IsGroupExpansion), m => Assert.Equal(0, m.RerankScore));
    }

    // ── Degraded operation ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_embedding_skips_the_vector_leg_but_the_keyword_leg_still_answers()
    {
        Seed("err", "Error 131047 re-engagement window.", OtherVector, "Err");
        var embedder = new FakeEmbedder { FailAlways = true };

        var result = await NewService(TenantA, embedder).Service.RetrieveAsync(Ask("131047"));

        Assert.Equal(0, result.Diagnostics.VectorHitCount);
        Assert.Contains(result.Evidence, e => e.ArticleKey == "err");
    }

    [Fact]
    public async Task A_failed_embedding_is_not_cached()
    {
        Seed("err", "Refund policy.", RefundVector, "Err");
        var embedder = new FakeEmbedder { FailAlways = true };
        var (service, _) = NewService(TenantA, embedder);
        await service.RetrieveAsync(Ask("refund policy"));

        embedder.FailAlways = false;
        _cache.Clear(prefix: "kb:res:");   // isolate the EMBEDDING cache: results are a separate cache

        var result = await service.RetrieveAsync(Ask("refund policy"));

        // Caching the empty vector would have turned one transient error into half an hour of no
        // vector search.
        Assert.True(result.Diagnostics.VectorHitCount > 0);
    }

    [Fact]
    public async Task An_empty_query_returns_a_clear_gate_failure_without_touching_the_stores()
    {
        var result = await NewService(TenantA).Service.RetrieveAsync(Ask("   "));

        Assert.Empty(result.Evidence);
        Assert.Equal("EmptyQuery", result.Diagnostics.GateFailureReason);
    }

    [Fact]
    public async Task Options_configured_below_the_safety_floor_refuse_to_run()
    {
        Seed("a", "Refund.", RefundVector, "A");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(TenantA, options: new SupportRagOptions { MinTopRerankScore = 0.2 }).Service.RetrieveAsync(Ask("refund")));
    }

    // ── Caching ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_repeated_question_is_served_from_the_result_cache()
    {
        Seed("a", "Refund policy.", RefundVector, "A");
        var (service, embedder) = NewService(TenantA);

        var first = await service.RetrieveAsync(Ask("refund policy"));
        var callsAfterFirst = embedder.Calls;
        var second = await service.RetrieveAsync(Ask("refund policy"));

        Assert.False(first.Diagnostics.ResultCacheHit);
        Assert.True(second.Diagnostics.ResultCacheHit);
        Assert.Equal(callsAfterFirst, embedder.Calls);   // no provider call, no search
    }

    [Fact]
    public async Task A_cached_result_for_one_tenant_is_never_served_to_another()
    {
        // The cross-tenant leak that needs no bug in any filter: a result cache keyed only by the
        // query would hand tenant A's private-article answer to tenant B asking the same question.
        Seed("a-secret", "Refund policy for A only.", RefundVector, "A secret", tenantId: TenantA);

        var asA = await NewService(TenantA).Service.RetrieveAsync(Ask("refund policy"));
        Assert.Contains(asA.Evidence, e => e.ArticleKey == "a-secret");

        var asB = await NewService(TenantB).Service.RetrieveAsync(Ask("refund policy"));

        Assert.False(asB.Diagnostics.ResultCacheHit);
        Assert.DoesNotContain(asB.Evidence, e => e.ArticleKey == "a-secret");
    }

    [Fact]
    public async Task Cache_keys_always_contain_the_tenant()
    {
        Assert.NotEqual(RetrievalCacheKeys.Result(TenantA, "q"), RetrievalCacheKeys.Result(TenantB, "q"));
        Assert.NotEqual(RetrievalCacheKeys.Embedding(TenantA, "p", "m", "q"), RetrievalCacheKeys.Embedding(TenantB, "p", "m", "q"));
        Assert.NotEqual(RetrievalCacheKeys.Result(TenantA, "q"), RetrievalCacheKeys.Result(null, "q"));
        await Task.CompletedTask;
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Diagnostics_explain_what_ran()
    {
        Seed("a", "Refund policy.", RefundVector, "A");

        var d = (await NewService(TenantA).Service.RetrieveAsync(
            Ask("refund policy") with { DetectedIntent = SupportIntent.RefundRequest })).Diagnostics;

        Assert.Equal("refund policy", d.NormalizedQuery);
        Assert.Equal(2, d.ExpandedQueries.Count);   // verbatim + canonical
        Assert.Equal("JsonColumnCosine", d.VectorStore);
        Assert.Equal("InApplicationBm25", d.KeywordStore);
        Assert.True(d.VectorHitCount > 0);
        Assert.True(d.KeywordHitCount > 0);
        Assert.True(d.FusedCount > 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private Guid _lastArticleId;

    private void Seed(
        string key, string text, float[] vector, string title,
        Guid? tenantId = null, bool global = false, KnowledgeArticleStatus status = KnowledgeArticleStatus.Published,
        bool isCurrent = true, string? countryCode = null, ProductModule? module = null, int? authority = null,
        string provider = "OpenAI", string model = "text-embedding-3-small",
        (Guid Id, int Seq, int Total)? group = null, bool sameArticleAsPrevious = false)
    {
        // An explicit tenant, else GLOBAL when asked for, else tenant A.
        Guid? owner = global ? null : (tenantId ?? TenantA);
        SeedCore(key, text, vector, title, owner, status, isCurrent, countryCode, module, authority, provider, model, group, sameArticleAsPrevious);
    }

    private void SeedCore(
        string key, string text, float[] vector, string title, Guid? owner, KnowledgeArticleStatus status, bool isCurrent,
        string? countryCode, ProductModule? module, int? authority, string provider, string model,
        (Guid Id, int Seq, int Total)? group, bool sameArticleAsPrevious)
    {
        var rank = authority ?? (owner is null ? 90 : 30);

        if (!sameArticleAsPrevious)
        {
            var article = new KnowledgeBaseArticle
            {
                TenantId = owner,
                ArticleKey = key,
                Title = title,
                Content = text,
                ContentHash = Guid.NewGuid().ToString("N"),
                SourceType = owner is null ? KnowledgeSourceType.PlatformPolicy : KnowledgeSourceType.AdminConfiguredArticle,
                AuthorityRank = rank,
                Status = status,
                IsCurrentVersion = isCurrent,
                LanguageCode = "en",
                CountryCode = countryCode,
                ProductModule = module,
                EffectiveFrom = Now.AddDays(-30),
                PublishedAt = Now.AddDays(-30),
                ApprovedBy = Guid.NewGuid(),
                ApprovedAt = Now.AddDays(-30)
            };
            _seedDb.KnowledgeBaseArticles.Add(article);
            _seedDb.SaveChanges();
            _lastArticleId = article.Id;
            _articleKeys[article.Id] = key;
        }

        _seedDb.KnowledgeBaseChunks.Add(new KnowledgeBaseChunk
        {
            TenantId = owner,
            ArticleId = _lastArticleId,
            ChunkIndex = 0,
            ContextHeader = title,
            ChunkText = text,
            EmbeddingInput = text,
            SearchText = title + " " + text,
            Embedding = SqlServerVectorStore.ToVectorLiteral(vector),
            EmbeddingProvider = provider,
            EmbeddingModel = model,
            ArticleStatus = status,
            IsActive = true,
            IsCurrentArticleVersion = isCurrent,
            EffectiveFrom = Now.AddDays(-30),
            CountryCode = countryCode,
            LanguageCode = "en",
            ProductModule = module,
            SourceType = owner is null ? KnowledgeSourceType.PlatformPolicy : KnowledgeSourceType.AdminConfiguredArticle,
            AuthorityRank = rank,
            AtomicGroupId = group?.Id,
            AtomicGroupSequence = group?.Seq,
            AtomicGroupTotal = group?.Total
        });
        _seedDb.SaveChanges();
    }

    private readonly Dictionary<Guid, string> _articleKeys = new();

    public void Dispose()
    {
        _seedDb.Dispose();
        _connection.Dispose();
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────────────

    private sealed class FakeEmbedder : IEmbeddingService
    {
        public string ProviderName => "OpenAI";
        public string ModelName => "text-embedding-3-small";
        public bool IsAvailable => true;
        public bool FailAlways { get; set; }
        public int Calls;

        public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (FailAlways)
                return Task.FromResult(Array.Empty<float>());

            var lower = text.ToLowerInvariant();
            return Task.FromResult(lower.Contains("refund") ? RefundVector : lower.Contains("credit") ? CreditVector : OtherVector);
        }
    }

    private sealed class FakeReranker : IReranker
    {
        public string ProviderName => "Fake";
        public Func<string, double> ScoreBy { get; set; } = _ => 0.5;

        public Task<IReadOnlyList<double>?> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<double>?>(documents.Select(ScoreBy).ToList());
    }

    private sealed class UnavailableReranker : IReranker
    {
        public string ProviderName => "None";

        public Task<IReadOnlyList<double>?> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<double>?>(null);
    }

    private sealed class DictionaryCache : IRetrievalCache
    {
        private readonly Dictionary<string, object> _items = new();

        public bool TryGet<T>(string key, out T? value)
        {
            if (_items.TryGetValue(key, out var stored) && stored is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public void Set<T>(string key, T value, TimeSpan ttl) => _items[key] = value!;

        public void Clear(string prefix)
        {
            foreach (var key in _items.Keys.Where(k => k.StartsWith(prefix)).ToList())
                _items.Remove(key);
        }
    }

    private sealed class StubTenant : ITenantContext
    {
        public StubTenant(Guid? tenantId, bool superAdmin = false)
        {
            TenantId = tenantId;
            IsPlatformSuperAdmin = superAdmin;
        }

        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin { get; }
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
