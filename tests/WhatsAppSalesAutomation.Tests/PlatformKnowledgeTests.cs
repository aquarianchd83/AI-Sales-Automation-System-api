using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Platform-authored (GLOBAL) knowledge: the articles the AI support agent uses when ANY tenant asks
/// about the product itself. The platform team writes them and the platform pays to embed them.
///
/// The property under test, throughout, is that platform knowledge lands in ONE known vector space
/// (the platform's) and is searched in it, whatever provider a tenant happens to use. Get that wrong
/// and the failure is silent: the article publishes fine, indexes fine, and is never found.
/// </summary>
public sealed class PlatformKnowledgeTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private const string RefundPolicy = "## Refunds\n\nRefunds are processed within 7 working days of an approved request. " +
                                        "This does not apply to annual plans, for which a pro-rata calculation is used.";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new() { UtcNow = Now };

    // The platform embeds with OpenAI; the TENANT under test embeds with Google - the case where the
    // two spaces differ and a naive single-embedder search cannot match.
    private readonly SpaceEmbedder _platformEmbedder = new("OpenAI", "text-embedding-3-small", refundAxis: 0);
    private readonly SpaceEmbedder _tenantEmbedder = new("Google", "text-embedding-004", refundAxis: 2);

    public PlatformKnowledgeTests()
    {
        _connection.Open();
        using var seed = Context(tenant: null, superAdmin: true);
        seed.Database.EnsureCreated();
    }

    private SqliteApplicationDbContext Context(Guid? tenant, bool superAdmin = false)
    {
        var stub = new StubTenant(tenant, superAdmin);
        return new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection)
                .AddInterceptors(new TenantStampingSaveChangesInterceptor(stub)).Options,
            stub, new AnonymousUser()) { StampTenantId = tenant };
    }

    private KnowledgeIngestionService Ingestion(SqliteApplicationDbContext db, IEmbeddingService active, IPlatformEmbeddingService? platform, params IEmbeddingService[] catalog) => new(
        db, active, new FakeCatalog(catalog.Length == 0 ? new[] { active } : catalog), new JsonColumnVectorStore(db),
        new StructureAwareChunker(new HeuristicTokenCounter()), new EmbeddingBatcher((_, _) => Task.CompletedTask),
        new HeuristicTokenCounter(), new NullQueue(), _clock, NullLogger<KnowledgeIngestionService>.Instance,
        retrieval: null, platformEmbedder: platform);

    private KnowledgeBaseService Kb(SqliteApplicationDbContext db, Guid? tenant, bool superAdmin, IPlatformEmbeddingService? platform, IEmbeddingService? embedder = null)
    {
        embedder ??= _tenantEmbedder;
        return new KnowledgeBaseService(
            db, _clock, new StubTenant(tenant, superAdmin), Fake.Of<IPlanLimitsService>((m, _) => Task.CompletedTask),
            embedder, new FakeCatalog(embedder), new ActiveProvider(),
            Fake.Of<ITenantConfigOverrideProvider>((m, _) => Task.FromResult(new AiOptions { MinRelevanceScore = 0, KnowledgeBaseTopN = 10 })),
            new CreateKnowledgeBaseArticleRequestValidator(), new UpdateKnowledgeBaseArticleRequestValidator(),
            new BulkPublishArticlesRequestValidator(), Ingestion(db, embedder, platform), new KnowledgeMetadataSyncService(db));
    }

    private KnowledgeRetrievalService Retrieval(SqliteApplicationDbContext db, Guid tenant, IPlatformEmbeddingService? platform) => new(
        db, _tenantEmbedder, new JsonColumnVectorStore(db), new Bm25KeywordStore(db), new NoReranker(), new NoCache(),
        new StubTenant(tenant), new HeuristicTokenCounter(), _clock, Options.Create(new SupportRagOptions()),
        NullLogger<KnowledgeRetrievalService>.Instance, platform);

    private static KnowledgeRetrievalRequest Ask(string query) => new(query, Array.Empty<string>(), null, null, "IN", "en", null);

    private async Task<Guid> PublishPlatformArticle(string title = "Refund policy", string content = RefundPolicy)
    {
        using var db = Context(null, superAdmin: true);
        var kb = Kb(db, null, true, _platformEmbedder);
        var created = await kb.CreateAsync(new CreateKnowledgeBaseArticleRequest(title, null, content, "RefundCancellationPolicy"));
        await kb.PublishAsync(created.Id, SuperAdmin);
        return created.Id;
    }

    // -- Indexing goes through the platform embedder ------------------------------------------

    [Fact]
    public async Task A_platform_article_is_created_as_global_with_platform_authority()
    {
        using var db = Context(null, superAdmin: true);
        var created = await Kb(db, null, true, _platformEmbedder)
            .CreateAsync(new CreateKnowledgeBaseArticleRequest("Refunds", null, RefundPolicy, "RefundCancellationPolicy"));

        var article = db.KnowledgeBaseArticles.AsNoTracking().Single(a => a.Id == created.Id);

        Assert.Null(article.TenantId);
        Assert.Equal(TenantKnowledgeScope.Global, article.TenantScope);
        Assert.Equal(90, article.AuthorityRank);   // above the 30 a tenant article is capped at
    }

    [Fact]
    public async Task A_platform_article_is_embedded_with_the_platform_provider_not_the_ambient_one()
    {
        var id = await PublishPlatformArticle();

        using var db = Context(null, superAdmin: true);
        var chunks = db.KnowledgeBaseChunks.AsNoTracking().Where(c => c.ArticleId == id).ToList();

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            Assert.Equal("OpenAI", c.EmbeddingProvider);      // the platform's, even though the ambient embedder is Google
            Assert.Equal("text-embedding-3-small", c.EmbeddingModel);
            Assert.Null(c.TenantId);
            Assert.True(c.IsActive);
        });
        Assert.Equal(0, _tenantEmbedder.Calls);   // the tenant's provider was never asked to pay for it
    }

    [Fact]
    public async Task A_platform_article_is_not_fanned_out_to_every_other_provider()
    {
        using var db = Context(null, superAdmin: true);
        var extra = new SpaceEmbedder("Google", "text-embedding-004", 2);
        var kb = new KnowledgeBaseService(
            db, _clock, new StubTenant(null, true), Fake.Of<IPlanLimitsService>((m, _) => Task.CompletedTask),
            _tenantEmbedder, new FakeCatalog(_tenantEmbedder, extra), new ActiveProvider(),
            Fake.Of<ITenantConfigOverrideProvider>((m, _) => Task.FromResult(new AiOptions())),
            new CreateKnowledgeBaseArticleRequestValidator(), new UpdateKnowledgeBaseArticleRequestValidator(), new BulkPublishArticlesRequestValidator(),
            Ingestion(db, _tenantEmbedder, _platformEmbedder, _tenantEmbedder, extra), new KnowledgeMetadataSyncService(db));

        var created = await kb.CreateAsync(new CreateKnowledgeBaseArticleRequest("R", null, RefundPolicy, "RefundCancellationPolicy"));
        await kb.PublishAsync(created.Id, SuperAdmin);

        // Tenant content fans out so a tenant on another provider still has vectors. Platform content
        // instead has the QUESTION embedded in the platform's space, so fanning out here would just be
        // the platform paying for vectors nobody ever reads.
        Assert.Equal(0, extra.Calls);
        Assert.All(db.KnowledgeBaseChunkEmbeddings.AsNoTracking().ToList(), e => Assert.Equal("OpenAI", e.Provider));
    }

    // -- Retrieval: the second, platform-space leg --------------------------------------------

    [Fact]
    public async Task A_tenant_on_a_different_provider_still_finds_platform_knowledge()
    {
        await PublishPlatformArticle();

        using var db = Context(TenantA);
        var result = await Retrieval(db, TenantA, _platformEmbedder).RetrieveAsync(Ask("refund"));

        // The tenant embeds with Google, the article is in OpenAI's space. Only the platform leg -
        // which embeds the question a second time, with the platform's model - can match them.
        Assert.Contains(result.Evidence, e => e.Title == "Refund policy");
        Assert.True(_platformEmbedder.Calls > 1, "the question should have been embedded in the platform's space too");
    }

    [Fact]
    public async Task Without_the_platform_leg_the_same_search_finds_nothing_which_is_the_bug_this_prevents()
    {
        await PublishPlatformArticle();

        using var db = Context(TenantA);
        var vectorOnlyEvidence = (await Retrieval(db, TenantA, platform: null).RetrieveAsync(Ask("zzz unrelated words"))).Evidence;

        // Also no keyword overlap by construction, so this isolates the vector leg: the article exists
        // and is Published, and a tenant on another provider simply cannot see it.
        Assert.DoesNotContain(vectorOnlyEvidence, e => e.Title == "Refund policy");
    }

    [Fact]
    public async Task A_tenant_already_in_the_platforms_space_needs_no_second_embedding()
    {
        await PublishPlatformArticle();
        var sameSpaceTenant = new SpaceEmbedder("OpenAI", "text-embedding-3-small", refundAxis: 0);
        var callsBeforeSearch = _platformEmbedder.Calls;   // publishing already used it, to embed the article

        using var db = Context(TenantA);
        var service = new KnowledgeRetrievalService(
            db, sameSpaceTenant, new JsonColumnVectorStore(db), new Bm25KeywordStore(db), new NoReranker(), new NoCache(),
            new StubTenant(TenantA), new HeuristicTokenCounter(), _clock, Options.Create(new SupportRagOptions()),
            NullLogger<KnowledgeRetrievalService>.Instance, _platformEmbedder);

        var result = await service.RetrieveAsync(Ask("refund"));

        Assert.Contains(result.Evidence, e => e.Title == "Refund policy");
        // The tenant's own leg already covers GLOBAL chunks in the shared space, so the platform's
        // embedder is never called: no double cost, no duplicate results.
        Assert.Equal(callsBeforeSearch, _platformEmbedder.Calls);
    }

    [Fact]
    public async Task Platform_and_tenant_knowledge_are_found_together_and_platform_policy_ranks_first()
    {
        await PublishPlatformArticle();

        using var db = Context(TenantA);
        var kb = Kb(db, TenantA, false, _platformEmbedder);
        var mine = await kb.CreateAsync(new CreateKnowledgeBaseArticleRequest("My refund note", null, RefundPolicy.Replace("7 working", "3 working"), "AdminConfiguredArticle"));
        await kb.PublishAsync(mine.Id, SuperAdmin);

        var evidence = (await Retrieval(db, TenantA, _platformEmbedder).RetrieveAsync(Ask("refund"))).Evidence;

        Assert.Contains(evidence, e => e.Title == "Refund policy");
        Assert.Contains(evidence, e => e.Title == "My refund note");
        // Authority 90 versus 30: on the same question the platform's policy has to win.
        Assert.Equal("Refund policy", evidence[0].Title);
    }

    [Fact]
    public async Task The_platform_leg_returns_only_platform_chunks()
    {
        await PublishPlatformArticle();
        using (var db = Context(TenantA))
        {
            // A tenant chunk that happens to be embedded in the platform's space.
            var kb = Kb(db, TenantA, false, _platformEmbedder, embedder: _platformEmbedder);
            var mine = await kb.CreateAsync(new CreateKnowledgeBaseArticleRequest("Tenant note", null, RefundPolicy, "AdminConfiguredArticle"));
            await kb.PublishAsync(mine.Id, SuperAdmin);
        }

        using var read = Context(TenantA);
        var hits = await new JsonColumnVectorStore(read).SearchAsync(
            new[] { 1f, 0f, 0f }, new RetrievalFilter
            {
                TenantId = TenantA, NowUtc = Now, TicketLanguageCode = "en", EmbeddingProvider = "OpenAI",
                EmbeddingModel = "text-embedding-3-small", GlobalOnly = true
            }, 50);

        var owners = hits.Select(h => read.KnowledgeBaseChunks.AsNoTracking().Single(c => c.Id == h.ChunkId).TenantId).ToList();

        Assert.NotEmpty(owners);
        Assert.All(owners, o => Assert.Null(o));
    }

    // -- Scoping the management screens -------------------------------------------------------

    [Fact]
    public async Task A_tenants_knowledge_base_never_lists_platform_articles()
    {
        await PublishPlatformArticle();
        using var db = Context(TenantA);
        var kb = Kb(db, TenantA, false, _platformEmbedder);
        await kb.CreateAsync(new CreateKnowledgeBaseArticleRequest("Mine", null, RefundPolicy, "AdminConfiguredArticle"));

        var page = await kb.GetPagedAsync(new Application.Common.Models.PagedRequest());

        // Retrieval reads platform knowledge for every tenant, but the tenant's MANAGEMENT screen must
        // not - or every tenant would browse (and try to edit) rows they cannot write.
        Assert.Equal(new[] { "Mine" }, page.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task A_tenant_cannot_fetch_a_platform_article_by_id()
    {
        var id = await PublishPlatformArticle();
        using var db = Context(TenantA);

        await Assert.ThrowsAsync<NotFoundException>(() => Kb(db, TenantA, false, _platformEmbedder).GetByIdAsync(id));
    }

    [Fact]
    public async Task The_platform_screen_lists_only_platform_articles()
    {
        await PublishPlatformArticle();
        using (var db = Context(TenantA))
            await Kb(db, TenantA, false, _platformEmbedder).CreateAsync(new CreateKnowledgeBaseArticleRequest("Tenant private", null, RefundPolicy, "AdminConfiguredArticle"));

        using var platform = Context(null, superAdmin: true);
        var page = await Kb(platform, null, true, _platformEmbedder).GetPagedAsync(new Application.Common.Models.PagedRequest());

        Assert.Equal(new[] { "Refund policy" }, page.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task A_tenant_cannot_author_a_platform_source_type()
    {
        using var db = Context(TenantA);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            Kb(db, TenantA, false, _platformEmbedder).CreateAsync(new CreateKnowledgeBaseArticleRequest("Fake policy", null, RefundPolicy, "PlatformPolicy")));

        Assert.Contains("platform knowledge", ex.Message);
    }

    // -- Deprecate ----------------------------------------------------------------------------

    [Fact]
    public async Task Deprecating_removes_the_article_from_every_tenants_answers_immediately()
    {
        var id = await PublishPlatformArticle();
        using (var db = Context(TenantA))
            Assert.Contains((await Retrieval(db, TenantA, _platformEmbedder).RetrieveAsync(Ask("refund"))).Evidence, e => e.ArticleId == id);

        using (var platform = Context(null, superAdmin: true))
        {
            var dto = await Kb(platform, null, true, _platformEmbedder).DeprecateAsync(id, "Refund window changed to 14 days");
            Assert.Equal("Deprecated", dto.Status);
        }

        using var after = Context(TenantA);
        // The chunks carry their own copy of the status and retrieval filters on THAT copy; without the
        // sync the article would say Deprecated while its text kept being served.
        Assert.DoesNotContain((await Retrieval(after, TenantA, _platformEmbedder).RetrieveAsync(Ask("refund"))).Evidence, e => e.ArticleId == id);
    }

    [Fact]
    public async Task Deprecating_requires_a_reason_and_records_it()
    {
        var id = await PublishPlatformArticle();
        using var platform = Context(null, superAdmin: true);
        var kb = Kb(platform, null, true, _platformEmbedder);

        await Assert.ThrowsAsync<ValidationException>(() => kb.DeprecateAsync(id, "  "));
        await kb.DeprecateAsync(id, "Superseded by the 2026 policy");

        Assert.Equal("Superseded by the 2026 policy", platform.KnowledgeBaseArticles.AsNoTracking().Single(a => a.Id == id).LifecycleNote);
    }

    [Fact]
    public async Task Deprecating_twice_is_harmless()
    {
        var id = await PublishPlatformArticle();
        using var platform = Context(null, superAdmin: true);
        var kb = Kb(platform, null, true, _platformEmbedder);

        await kb.DeprecateAsync(id, "first");
        var second = await kb.DeprecateAsync(id, "second");

        Assert.Equal("Deprecated", second.Status);
        Assert.Equal("first", platform.KnowledgeBaseArticles.AsNoTracking().Single(a => a.Id == id).LifecycleNote);   // not overwritten
    }

    // -- Reindex ------------------------------------------------------------------------------

    [Fact]
    public async Task Reindexing_from_the_platform_keeps_platform_articles_in_the_platform_space()
    {
        var id = await PublishPlatformArticle();
        using var platform = Context(null, superAdmin: true);
        var kb = Kb(platform, null, true, _platformEmbedder);
        await kb.UpdateAsync(id, new UpdateKnowledgeBaseArticleRequest("Refund policy", null, RefundPolicy + "\n\n## Extra\n\nNew section about credits."));

        await kb.ReindexAsync();

        // Through the old in-line re-embed this would have used the ambient (tenantless, so Simulated)
        // embedder and quietly moved the article out of the space every tenant searches.
        var chunks = platform.KnowledgeBaseChunks.AsNoTracking().Where(c => c.ArticleId == id && c.IsActive).ToList();
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal("OpenAI", c.EmbeddingProvider));
    }

    [Fact]
    public async Task A_tenant_reindex_does_not_touch_platform_articles()
    {
        var id = await PublishPlatformArticle();
        using (var platform = Context(null, superAdmin: true))
        {
            var kb = Kb(platform, null, true, _platformEmbedder);
            await kb.UpdateAsync(id, new UpdateKnowledgeBaseArticleRequest("Refund policy", null, RefundPolicy + "\n\nMore."));   // now stale
        }
        var callsBefore = _platformEmbedder.Calls;

        using var tenant = Context(TenantA);
        await Kb(tenant, TenantA, false, _platformEmbedder).ReindexAsync();

        Assert.Equal(callsBefore, _platformEmbedder.Calls);
    }

    // -- The platform embedder itself ---------------------------------------------------------

    private static PlatformEmbeddingService Service(AiProviderSettings settings) => new(
        new FixedOptions<AiProviderSettings>(settings),
        new OpenAiEmbeddingClient(new HttpClient(), NullLogger<OpenAiEmbeddingClient>.Instance),
        new GoogleEmbeddingClient(new HttpClient(), NullLogger<GoogleEmbeddingClient>.Instance),
        new SimulatedEmbeddingClient());

    [Fact]
    public void An_openai_platform_setting_with_a_key_is_a_real_openai_embedder()
    {
        var service = Service(new AiProviderSettings { EmbeddingProvider = "OpenAI", OpenAI = { ApiKey = "sk-test", EmbeddingModel = "text-embedding-3-small" } });

        Assert.Equal("OpenAI", service.ProviderName);
        Assert.Equal("text-embedding-3-small", service.ModelName);
        Assert.False(service.IsSimulated);
        Assert.False(service.RealProviderRequestedButUnconfigured);
    }

    [Fact]
    public void A_real_provider_with_no_key_falls_back_and_says_it_is_a_misconfiguration()
    {
        var service = Service(new AiProviderSettings { EmbeddingProvider = "OpenAI" });

        Assert.Equal("Simulated", service.ProviderName);
        Assert.True(service.IsSimulated);
        // Distinguishable from CHOOSING Simulated, because it is almost always a mistake.
        Assert.True(service.RealProviderRequestedButUnconfigured);
    }

    [Fact]
    public void Choosing_simulated_on_purpose_is_not_flagged_as_a_misconfiguration()
    {
        var service = Service(new AiProviderSettings { EmbeddingProvider = "Simulated" });

        Assert.True(service.IsSimulated);
        Assert.False(service.RealProviderRequestedButUnconfigured);
    }

    [Fact]
    public async Task The_simulated_platform_embedder_still_produces_vectors()
    {
        var vector = await Service(new AiProviderSettings()).GetEmbeddingAsync("refund policy");

        Assert.NotEmpty(vector);
    }

    public void Dispose() => _connection.Dispose();

    // -- Fakes --------------------------------------------------------------------------------

    /// <summary>An embedder with its own vector space: "refund" points along one chosen axis, so two of
    /// these with different axes produce vectors that are NOT comparable - which is the whole problem.</summary>
    private sealed class SpaceEmbedder : IPlatformEmbeddingService
    {
        private readonly int _axis;

        public SpaceEmbedder(string provider, string model, int refundAxis)
        {
            ProviderName = provider;
            ModelName = model;
            _axis = refundAxis;
        }

        public string ProviderName { get; }
        public string ModelName { get; }
        public bool IsAvailable => true;
        public bool IsSimulated => false;
        public bool RealProviderRequestedButUnconfigured => false;
        public int Calls;

        public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var v = new float[3];
            v[text.Contains("efund", StringComparison.OrdinalIgnoreCase) ? _axis : (_axis + 1) % 3] = 1f;
            return Task.FromResult(v);
        }
    }

    private sealed class FakeCatalog : IEmbeddingProviderCatalog
    {
        public FakeCatalog(params IEmbeddingService[] providers) => AllProviders = providers;
        public IReadOnlyList<IEmbeddingService> AllProviders { get; }
    }

    private sealed class ActiveProvider : IActiveAiProviderAccessor
    {
        string IActiveAiProviderAccessor.ActiveProvider => "Simulated";
        public bool HasApiKey(string provider) => false;
    }

    private sealed class NullQueue : IKnowledgeIngestionQueue
    {
        public void Enqueue(Guid jobId, Guid? tenantId, bool securityReviewed) { }
    }

    private sealed class NoReranker : IReranker
    {
        public string ProviderName => "None";
        public Task<IReadOnlyList<double>?> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<double>?>(null);
    }

    private sealed class NoCache : IRetrievalCache
    {
        public bool TryGet<T>(string key, out T? value) { value = default; return false; }
        public void Set<T>(string key, T value, TimeSpan ttl) { }
    }

    private sealed class StubTenant : ITenantContext
    {
        public StubTenant(Guid? tenantId, bool superAdmin = false) { TenantId = tenantId; IsPlatformSuperAdmin = superAdmin; }
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin { get; }
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
