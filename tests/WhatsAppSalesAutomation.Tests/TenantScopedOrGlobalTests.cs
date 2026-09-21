using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The isolation rules for knowledge that can be platform-owned.
///
/// These are the tests that matter most in Phase 6. Everything else in the knowledge pipeline is a
/// quality problem - a worse answer, a missed article. A mistake here is a tenant reading another
/// tenant's private data, or a tenant authoring something the AI will quote to everyone else as
/// platform policy. Both failures are silent: the query returns rows, the save succeeds.
/// </summary>
public sealed class TenantScopedOrGlobalTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TenantScopedOrGlobalTests()
    {
        _connection.Open();

        // One schema, created once as the platform, then read back through each tenant's own context
        // so the query filter is what differs between them rather than the data.
        using var seed = NewContext(new StubTenant(null, isSuperAdmin: true));
        seed.Database.EnsureCreated();

        seed.KnowledgeBaseArticles.AddRange(
            Article("global-refund-policy", tenantId: null, KnowledgeSourceType.RefundCancellationPolicy),
            Article("tenant-a-private", TenantA, KnowledgeSourceType.AdminConfiguredArticle),
            Article("tenant-b-private", TenantB, KnowledgeSourceType.AdminConfiguredArticle));
        seed.SaveChanges();
    }

    // ── Reading ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_tenant_sees_global_knowledge_and_its_own()
    {
        using var db = NewContext(new StubTenant(TenantA));

        var keys = db.KnowledgeBaseArticles.Select(a => a.ArticleKey).OrderBy(k => k).ToList();

        Assert.Equal(new[] { "global-refund-policy", "tenant-a-private" }, keys);
    }

    [Fact]
    public void A_tenant_never_sees_another_tenants_knowledge()
    {
        using var db = NewContext(new StubTenant(TenantA));

        Assert.DoesNotContain("tenant-b-private", db.KnowledgeBaseArticles.Select(a => a.ArticleKey).ToList());

        // Also by direct lookup, not only by listing - a filter that only covered the listing path
        // would still hand over a row to anyone who knew its key.
        Assert.Null(db.KnowledgeBaseArticles.FirstOrDefault(a => a.ArticleKey == "tenant-b-private"));
    }

    [Fact]
    public void A_superadmin_sees_global_knowledge_and_no_tenants_private_knowledge()
    {
        // The deliberate consequence of the same filter: with no tenant in scope the second branch
        // collapses to "TenantId IS NULL". A SuperAdmin editing platform policy should not have one
        // tenant's private articles silently mixed into the list.
        using var db = NewContext(new StubTenant(null, isSuperAdmin: true));

        Assert.Equal(new[] { "global-refund-policy" }, db.KnowledgeBaseArticles.Select(a => a.ArticleKey).ToList());
    }

    [Fact]
    public void Soft_deleted_global_knowledge_is_hidden_from_everyone()
    {
        using (var platform = NewContext(new StubTenant(null, isSuperAdmin: true)))
        {
            var article = platform.KnowledgeBaseArticles.Single(a => a.ArticleKey == "global-refund-policy");
            article.IsDeleted = true;
            platform.SaveChanges();
        }

        using var db = NewContext(new StubTenant(TenantA));

        // The scoped-or-global filter has to combine with soft delete, exactly as the tenant-owned
        // one does. Getting that wrong resurrects retracted platform policy for every tenant at once.
        Assert.Equal(new[] { "tenant-a-private" }, db.KnowledgeBaseArticles.Select(a => a.ArticleKey).ToList());
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_tenants_article_is_stamped_with_its_tenant_rather_than_becoming_global()
    {
        // The dangerous default. TenantId is nullable now, and NULL means "platform policy" - so an
        // unstamped insert is not a harmless blank, it is a tenant publishing to every other tenant.
        using var db = NewContext(new StubTenant(TenantA));

        db.KnowledgeBaseArticles.Add(Article("new-tenant-article", tenantId: null, KnowledgeSourceType.AdminConfiguredArticle));
        db.SaveChanges();

        var saved = db.KnowledgeBaseArticles.Single(a => a.ArticleKey == "new-tenant-article");
        Assert.Equal(TenantA, saved.TenantId);
    }

    [Fact]
    public void A_superadmin_may_author_global_knowledge()
    {
        using var db = NewContext(new StubTenant(null, isSuperAdmin: true));

        db.KnowledgeBaseArticles.Add(Article("new-global-policy", tenantId: null, KnowledgeSourceType.PlatformPolicy));
        db.SaveChanges();

        Assert.Null(db.KnowledgeBaseArticles.Single(a => a.ArticleKey == "new-global-policy").TenantId);
    }

    [Fact]
    public void A_tenant_cannot_insert_an_article_for_another_tenant()
    {
        using var db = NewContext(new StubTenant(TenantA));

        db.KnowledgeBaseArticles.Add(Article("smuggled", TenantB, KnowledgeSourceType.AdminConfiguredArticle));

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("while acting as tenant", ex.Message);
    }

    [Fact]
    public void A_tenant_cannot_promote_its_own_article_to_global()
    {
        using var db = NewContext(new StubTenant(TenantA));

        var article = db.KnowledgeBaseArticles.Single(a => a.ArticleKey == "tenant-a-private");
        article.TenantId = null;

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("only a PlatformSuperAdmin", ex.Message);
    }

    [Fact]
    public void A_tenant_cannot_take_a_copy_of_global_knowledge_for_itself()
    {
        // The other direction, and the easier one to forget. Claiming a GLOBAL article would remove
        // it from every other tenant's reach - a denial of service dressed up as an edit.
        using var db = NewContext(new StubTenant(TenantA));

        var article = db.KnowledgeBaseArticles.Single(a => a.ArticleKey == "global-refund-policy");
        article.TenantId = TenantA;

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("only a PlatformSuperAdmin", ex.Message);
    }

    [Fact]
    public void An_unattributed_write_with_no_tenant_and_no_superadmin_is_refused()
    {
        // A background job that forgot SetTenant. Under ITenantOwned this already threw; here it has
        // to keep throwing rather than quietly writing NULL, which the schema would happily accept
        // and which would mean "platform policy".
        using var db = NewContext(new StubTenant(null, isSuperAdmin: false));

        db.KnowledgeBaseArticles.Add(Article("orphan", tenantId: null, KnowledgeSourceType.AdminConfiguredArticle));

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("without a tenant in scope", ex.Message);
    }

    // ── Model-level assertion (backlog 0.2) ──────────────────────────────────────────────

    [Fact]
    public void No_entity_implements_both_tenancy_interfaces()
    {
        // EF Core keeps only the LAST query filter registered per entity and discards the other
        // without a word, so an entity implementing both would silently get one rule enforced and
        // the other not. ApplicationDbContext throws while building the model; this test states the
        // invariant in a place that names it, so a future entity declaring both fails here with an
        // explanation rather than in whichever unrelated test happens to touch the model first.
        using var db = NewContext(new StubTenant(TenantA));

        var offenders = db.Model.GetEntityTypes()
            .Select(e => e.ClrType)
            .Where(t => typeof(ITenantOwned).IsAssignableFrom(t) && typeof(ITenantScopedOrGlobal).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Knowledge_entities_are_all_scoped_or_global()
    {
        // An article can be GLOBAL, so everything hanging off it must be able to be too. A chunk
        // that stayed ITenantOwned would make a platform article's chunks invisible to every tenant -
        // the article would be found and then have nothing to say.
        using var db = NewContext(new StubTenant(TenantA));

        foreach (var type in new[]
                 {
                     typeof(KnowledgeBaseArticle),
                     typeof(KnowledgeBaseChunk),
                     typeof(KnowledgeBaseChunkEmbedding),
                     typeof(KnowledgeBaseArticleModelPublication),
                     typeof(KnowledgeBaseArticleVersion)
                 })
        {
            Assert.True(typeof(ITenantScopedOrGlobal).IsAssignableFrom(type), $"{type.Name} must be {nameof(ITenantScopedOrGlobal)}.");
            Assert.NotNull(db.Model.FindEntityType(type));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private SqliteApplicationDbContext NewContext(ITenantContext tenant)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            // The interceptor under test. The other Sqlite-backed suites stamp by hand through
            // SqliteApplicationDbContext.StampTenantId; here the real interceptor is wired in,
            // because its refusals ARE the isolation rule being tested.
            .AddInterceptors(new TenantStampingSaveChangesInterceptor(tenant))
            .Options;

        return new SqliteApplicationDbContext(options, tenant, new AnonymousUser());
    }

    private static KnowledgeBaseArticle Article(string key, Guid? tenantId, KnowledgeSourceType sourceType) => new()
    {
        TenantId = tenantId,   // TenantScope follows this - see KnowledgeBaseArticle.TenantId
        ArticleKey = key,
        Title = key,
        Content = "body",
        ContentHash = key,
        SourceType = sourceType,
        AuthorityRank = tenantId is null ? 90 : 30,
        Status = KnowledgeArticleStatus.Draft,
        LanguageCode = "en",
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class StubTenant : ITenantContext
    {
        public StubTenant(Guid? tenantId, bool isSuperAdmin = false)
        {
            TenantId = tenantId;
            IsPlatformSuperAdmin = isSuperAdmin;
        }

        public Guid? TenantId { get; private set; }

        public bool IsPlatformSuperAdmin { get; }

        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    public void Dispose() => _connection.Dispose();
}
