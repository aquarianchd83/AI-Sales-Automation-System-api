using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The SOURCE index: a tenant's source passages embedded for drafting. Embedded once per distinct text, re-embedded only when the text or the
/// embedding provider/model changes, resumable after a provider outage, never stored for the simulated provider, and never mixing tenants.
/// </summary>
public sealed class SourcePassageIndexTests : IDisposable
{
    private sealed class TenantScope : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    /// <summary>A deterministic "meaning" vector from the text (so equal text gives an equal vector) that records every text it was asked to embed.</summary>
    private sealed class FakeEmbedder : IEmbeddingService
    {
        public string ProviderName { get; set; } = "OpenAI";
        public string ModelName { get; set; } = "model-1";
        public bool IsAvailable { get; set; } = true;
        public int Dimensions { get; set; } = 4;
        public List<string> Texts { get; } = new();

        /// <summary>When set, calls fail (an empty vector, the provider's way of saying so) once this many texts have been embedded.</summary>
        public int? FailAfter { get; set; }

        public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            if (FailAfter is { } n && Texts.Count >= n)
                return Task.FromResult(Array.Empty<float>());
            lock (Texts)
                Texts.Add(text);
            var v = new float[Dimensions];
            for (var i = 0; i < text.Length; i++)
                v[i % Dimensions] += text[i];
            return Task.FromResult(v);
        }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public SourcePassageIndexTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _tenant.TenantId = _a;
        _db = new SqliteApplicationDbContext(options, _tenant, new NoUser());
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private SourcePassageIndex Index() =>
        new(_db, _embedder, NullLogger<SourcePassageIndex>.Instance, new EmbeddingBatcher((_, _) => Task.CompletedTask));

    private async Task<KnowledgeSource> Source(Guid tenant, params string[] passages)
    {
        var source = new KnowledgeSource { TenantId = tenant, Name = "brochure.pdf", Kind = KnowledgeSourceKind.Document, Status = KnowledgeSourceStatus.Ready, Version = 1, PassageCount = passages.Length };
        _db.KnowledgeSources.Add(source);
        await AddPassages(source, 1, passages);
        return source;
    }

    private async Task AddPassages(KnowledgeSource source, int version, params string[] passages)
    {
        var order = 0;
        foreach (var text in passages)
            _db.SourcePassages.Add(new SourcePassage { TenantId = source.TenantId, SourceId = source.Id, SourceVersion = version, Order = order++, Text = text, Hash = PassageSplitter.HashOf(text), ExtractedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
    }

    private Task<List<SourcePassageEmbedding>> Rows(Guid tenant) => _db.SourcePassageEmbeddings.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenant).ToListAsync();

    [Fact]
    public async Task Passages_are_embedded_once_with_the_provider_model_and_size_recorded()
    {
        await Source(_a, "The consultation fee is Rs 500.", "Parking is free for patients.");
        var before = DateTime.UtcNow.AddSeconds(-5);

        var result = await Index().EnsureIndexedAsync(_a);

        Assert.Equal(("Complete", 2, 0, 0), (result.Status, result.Embedded, result.Reused, result.Pending));
        var rows = await Rows(_a);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(("OpenAI", "model-1", 4), (r.Provider, r.Model, r.Dimensions)));
        Assert.All(rows, r => Assert.InRange(r.EmbeddedAt, before, DateTime.UtcNow.AddSeconds(5))); // stored as UTC
        Assert.Equal(2, _embedder.Texts.Count);
    }

    [Fact]
    public async Task A_second_run_with_nothing_changed_embeds_nothing()
    {
        await Source(_a, "The consultation fee is Rs 500.", "Parking is free for patients.");
        await Index().EnsureIndexedAsync(_a);
        _embedder.Texts.Clear();

        var result = await Index().EnsureIndexedAsync(_a);

        Assert.Equal(("Complete", 0, 2), (result.Status, result.Embedded, result.Reused));
        Assert.Empty(_embedder.Texts);
    }

    [Fact]
    public async Task A_re_read_source_re_embeds_only_the_passages_whose_text_changed()
    {
        var source = await Source(_a, "The consultation fee is Rs 500.", "Parking is free for patients.");
        await Index().EnsureIndexedAsync(_a);
        _embedder.Texts.Clear();

        // The re-read replaces every passage row (new ids); one text is unchanged, one is new.
        _db.SourcePassages.RemoveRange(await _db.SourcePassages.Where(p => p.SourceId == source.Id).ToListAsync());
        await _db.SaveChangesAsync();
        source.Version = 2;
        await AddPassages(source, 2, "The consultation fee is Rs 500.", "Parking costs Rs 20 per hour now.");

        var result = await Index().EnsureIndexedAsync(_a);

        Assert.Equal((1, 1), (result.Embedded, result.Reused));
        Assert.Equal("Parking costs Rs 20 per hour now.", Assert.Single(_embedder.Texts));
        var rows = await Rows(_a);
        Assert.Equal(2, rows.Count); // the vector of the text that no longer exists is gone
        Assert.DoesNotContain(rows, r => r.PassageHash == PassageSplitter.HashOf("Parking is free for patients."));
    }

    [Fact]
    public async Task A_change_of_embedding_provider_or_model_re_embeds_everything_and_removes_the_old_vectors()
    {
        await Source(_a, "The consultation fee is Rs 500.", "Parking is free for patients.");
        await Index().EnsureIndexedAsync(_a);
        _embedder.Texts.Clear();

        _embedder.ModelName = "model-2";
        _embedder.Dimensions = 8;
        var onNewModel = await Index().EnsureIndexedAsync(_a);

        Assert.Equal((2, 0), (onNewModel.Embedded, onNewModel.Reused));
        var rows = await Rows(_a);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(("model-2", 8), (r.Model, r.Dimensions)));

        _embedder.ProviderName = "Google";
        var onNewProvider = await Index().EnsureIndexedAsync(_a);
        Assert.Equal(2, onNewProvider.Embedded);
        Assert.All(await Rows(_a), r => Assert.Equal("Google", r.Provider));
    }

    [Fact]
    public async Task The_simulated_provider_stores_nothing_and_leaves_drafting_to_word_search()
    {
        await Source(_a, "The consultation fee is Rs 500.");
        _embedder.ProviderName = "Simulated";

        var result = await Index().EnsureIndexedAsync(_a);

        Assert.Equal("SkippedSimulated", result.Status);
        Assert.Empty(await Rows(_a));
        Assert.Empty(_embedder.Texts);
        Assert.Null(await Index().LoadVectorsAsync(_a));
    }

    [Fact]
    public async Task A_provider_without_a_key_is_treated_like_the_simulator()
    {
        await Source(_a, "The consultation fee is Rs 500.");
        _embedder.IsAvailable = false;

        Assert.Equal("SkippedSimulated", (await Index().EnsureIndexedAsync(_a)).Status);
        Assert.Null(await Index().LoadVectorsAsync(_a));
    }

    [Fact]
    public async Task An_outage_part_way_keeps_what_was_done_and_the_next_run_finishes_the_rest()
    {
        var texts = Enumerable.Range(0, 100).Select(i => $"Passage number {i} about service {i}.").ToArray();
        await Source(_a, texts);
        _embedder.FailAfter = 96; // the first batch of 96 succeeds, the next fails

        var first = await Index().EnsureIndexedAsync(_a);

        Assert.Equal(("Partial", 96, 4), (first.Status, first.Embedded, first.Pending));
        Assert.Equal(96, (await Rows(_a)).Count);

        _embedder.FailAfter = null;
        _embedder.Texts.Clear();
        var second = await Index().EnsureIndexedAsync(_a);

        Assert.Equal(("Complete", 4, 96), (second.Status, second.Embedded, second.Reused));
        Assert.Equal(4, _embedder.Texts.Count); // only the missing ones were sent again
        Assert.Equal(100, (await Rows(_a)).Count);
    }

    [Fact]
    public async Task Another_tenants_passages_are_never_embedded_compared_or_returned()
    {
        await Source(_a, "Tenant A sells eye surgery packages.");
        await Source(_b, "Tenant B sells secret wholesale prices.");

        await Index().EnsureIndexedAsync(_a);

        Assert.DoesNotContain(_embedder.Texts, t => t.Contains("Tenant B"));
        Assert.Empty(await Rows(_b));

        var vectorsA = await Index().LoadVectorsAsync(_a);
        Assert.NotNull(vectorsA);
        Assert.Equal(new[] { PassageSplitter.HashOf("Tenant A sells eye surgery packages.") }, vectorsA!.ByHash.Keys.ToArray());

        // Indexing B embeds B's text under B only; the same text in both tenants is two rows, one each.
        _tenant.TenantId = _b;
        await Source(_b, "Tenant A sells eye surgery packages.");
        await Index().EnsureIndexedAsync(_b);
        Assert.Equal(2, (await Rows(_b)).Count);
        Assert.Equal(2, (await Index().LoadVectorsAsync(_b))!.ByHash.Count);
        _tenant.TenantId = _a;
        Assert.Single((await Index().LoadVectorsAsync(_a))!.ByHash);
        Assert.Single(await Rows(_a));
        Assert.Null(await Index().LoadVectorsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Vectors_are_returned_for_the_provider_in_use_only_and_a_query_is_embedded_with_it()
    {
        await Source(_a, "The consultation fee is Rs 500.");
        await Index().EnsureIndexedAsync(_a);

        var vectors = await Index().LoadVectorsAsync(_a);
        Assert.NotNull(vectors);
        Assert.Equal(("OpenAI", "model-1"), (vectors!.Provider, vectors.Model));
        var query = await vectors.EmbedQueryAsync("fee?");
        Assert.NotNull(query);
        Assert.Equal(1.0, Math.Sqrt(query!.Sum(x => (double)x * x)), 3); // unit length, so similarity is a dot product

        _embedder.ModelName = "model-2"; // the provider was changed since: the stored vectors no longer apply
        Assert.Null(await Index().LoadVectorsAsync(_a));
    }

    [Fact]
    public async Task A_failing_provider_while_embedding_a_question_gives_no_vector_not_an_error()
    {
        await Source(_a, "The consultation fee is Rs 500.");
        await Index().EnsureIndexedAsync(_a);
        var vectors = await Index().LoadVectorsAsync(_a);

        _embedder.FailAfter = 0;

        Assert.Null(await vectors!.EmbedQueryAsync("fee?"));
    }
}
