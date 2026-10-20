using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <param name="Status">Complete, Partial (the provider stopped part-way; progress is kept and the next run resumes), SkippedSimulated or Failed.</param>
public sealed record PassageIndexResult(string Status, int Embedded, int Reused, int Pending, string? Detail = null);

/// <summary>
/// The vectors of one tenant's current source passages for the embedding provider and model in use now, plus a way to embed a question with
/// that same provider. Passages with no vector (not embedded yet) are simply absent: they can still be found by their words.
/// </summary>
public sealed class PassageVectors
{
    private readonly Func<string, CancellationToken, Task<float[]?>> _embedQuery;

    public PassageVectors(string provider, string model, IReadOnlyDictionary<string, float[]> byHash, Func<string, CancellationToken, Task<float[]?>> embedQuery)
    {
        Provider = provider;
        Model = model;
        ByHash = byHash;
        _embedQuery = embedQuery;
    }

    public string Provider { get; }

    public string Model { get; }

    /// <summary>Normalised vectors by <see cref="SourcePassage.Hash"/>.</summary>
    public IReadOnlyDictionary<string, float[]> ByHash { get; }

    /// <summary>The question as a normalised vector, or null when the provider gave nothing (then the words alone decide).</summary>
    public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) => _embedQuery(text, cancellationToken);
}

/// <summary>
/// The SOURCE index: embeddings of the passages of a tenant's own sources, used only to find the passages that bear on a question when
/// drafting answers. It is a separate store from the chat's KnowledgeBaseChunks and chat never reads it.
/// </summary>
public interface ISourcePassageIndex
{
    /// <summary>Embeds the tenant's current passages that have no vector for the provider and model in use. Resumable and idempotent: what is
    /// already embedded is reused (by text hash), progress is saved batch by batch, and vectors made by another provider or model are removed.</summary>
    Task<PassageIndexResult> EnsureIndexedAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The tenant's stored vectors for the provider and model in use, or null when there are none to use (simulated provider, provider
    /// without credentials, nothing embedded yet) - the caller then ranks by words alone.</summary>
    Task<PassageVectors?> LoadVectorsAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The platform pays for these embeddings (the tenant is not charged) and uses the platform's embedding provider. The simulated provider's
/// hashing vectors say nothing about meaning, so with it nothing is stored and drafting falls back to word search.
/// </summary>
public sealed class SourcePassageIndex : ISourcePassageIndex
{
    public const string SimulatedProvider = "Simulated";

    /// <summary>Most passages loaded at once, the same ceiling the drafting service uses.</summary>
    private const int MaxPassages = 4000;

    private const int MaxEmbeddedChars = 6000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IApplicationDbContext _db;
    private readonly IEmbeddingService _embeddings;
    private readonly EmbeddingBatcher _batcher;
    private readonly ILogger<SourcePassageIndex> _logger;

    public SourcePassageIndex(IApplicationDbContext db, IEmbeddingService embeddings, ILogger<SourcePassageIndex> logger, EmbeddingBatcher? batcher = null)
    {
        _db = db;
        _embeddings = embeddings;
        _logger = logger;
        _batcher = batcher ?? new EmbeddingBatcher();
    }

    private bool UsesRealProvider =>
        !string.Equals(_embeddings.ProviderName, SimulatedProvider, StringComparison.OrdinalIgnoreCase) && _embeddings.IsAvailable;

    public async Task<PassageIndexResult> EnsureIndexedAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (!UsesRealProvider)
            return new PassageIndexResult("SkippedSimulated", 0, 0, 0, "The embedding provider is the simulator or has no key, so passages are searched by their words.");

        var provider = _embeddings.ProviderName;
        var model = _embeddings.ModelName;
        var passages = await CurrentPassagesAsync(tenantId, ct);
        var current = passages.Select(p => p.Hash).ToHashSet();

        // Vectors from another provider or model, or for text that is no longer in any source, are of no further use.
        var existing = await _db.SourcePassageEmbeddings.Where(e => e.TenantId == tenantId).ToListAsync(ct);
        var stale = existing.Where(e => e.Provider != provider || e.Model != model || !current.Contains(e.PassageHash)).ToList();
        if (stale.Count > 0)
        {
            _db.SourcePassageEmbeddings.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
        }

        var have = existing.Except(stale).Select(e => e.PassageHash).ToHashSet();
        var todo = passages.GroupBy(p => p.Hash).Select(g => g.First()).Where(p => !have.Contains(p.Hash)).ToList();
        var reused = current.Count - todo.Count;
        var embedded = 0;

        foreach (var batch in todo.Chunk(EmbeddingBatcher.MaxBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<float[]> vectors;
            try
            {
                var texts = batch.Select(EmbeddedText).ToList();
                vectors = await _batcher.EmbedBatchAsync(_embeddings, texts, ct);
            }
            catch (EmbeddingProviderUnavailableException ex)
            {
                _logger.LogWarning(ex, "Source passage embedding stopped for tenant {TenantId}; {Embedded} done, the rest resumes next time", tenantId, embedded);
                return new PassageIndexResult("Partial", embedded, reused, todo.Count - embedded, "The embedding provider did not answer. What is done is kept.");
            }

            var rows = new List<SourcePassageEmbedding>();
            for (var i = 0; i < batch.Length; i++)
            {
                rows.Add(new SourcePassageEmbedding
                {
                    TenantId = tenantId, PassageHash = batch[i].Hash, Provider = provider, Model = model, Dimensions = vectors[i].Length,
                    Embedding = JsonSerializer.Serialize(vectors[i], Json), EmbeddedAt = DateTime.UtcNow
                });
            }
            _db.SourcePassageEmbeddings.AddRange(rows);

            try
            {
                await _db.SaveChangesAsync(ct);
                embedded += batch.Length;
            }
            catch (DbUpdateException ex)
            {
                // Another indexer stored the same vectors at the same moment: nothing is lost, its rows serve.
                _logger.LogInformation(ex, "Source passage vectors for tenant {TenantId} were written concurrently", tenantId);
                _db.SourcePassageEmbeddings.RemoveRange(rows); // removing an unsaved row just forgets it
            }
        }

        return new PassageIndexResult("Complete", embedded, reused, 0);
    }

    public async Task<PassageVectors?> LoadVectorsAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (!UsesRealProvider)
            return null;

        var provider = _embeddings.ProviderName;
        var model = _embeddings.ModelName;
        var rows = await _db.SourcePassageEmbeddings.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Provider == provider && e.Model == model).ToListAsync(ct);
        if (rows.Count == 0)
            return null;

        var byHash = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                if (JsonSerializer.Deserialize<float[]>(row.Embedding, Json) is { Length: > 0 } vector)
                    byHash[row.PassageHash] = vector;
            }
            catch (JsonException)
            {
                // an unreadable row is the same as a missing one: that passage is found by its words
            }
        }

        return byHash.Count == 0 ? null : new PassageVectors(provider, model, byHash, EmbedQueryAsync);
    }

    private async Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct)
    {
        try
        {
            var vector = await _embeddings.GetEmbeddingAsync(text, ct);
            return vector.Length == 0 ? null : EmbeddingBatcher.Normalize(vector);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Embedding a question failed; its passages are searched by words only");
            return null;
        }
    }

    /// <summary>The passages that drafting reads: those of the tenant's Ready sources, at each source's current version.</summary>
    private async Task<List<SourcePassage>> CurrentPassagesAsync(Guid tenantId, CancellationToken ct)
    {
        var sources = await _db.KnowledgeSources.AsNoTracking().Where(s => s.TenantId == tenantId && s.Status == KnowledgeSourceStatus.Ready).ToListAsync(ct);
        var ids = sources.Select(s => s.Id).ToList();
        var passages = await _db.SourcePassages.AsNoTracking().Where(p => p.TenantId == tenantId && ids.Contains(p.SourceId))
            .OrderBy(p => p.SourceId).ThenBy(p => p.Order).Take(MaxPassages).ToListAsync(ct);
        return passages.Where(p => sources.First(s => s.Id == p.SourceId).Version == p.SourceVersion).ToList();
    }

    private static string EmbeddedText(SourcePassage p)
    {
        var text = string.IsNullOrWhiteSpace(p.Heading) ? p.Text : $"{p.Heading}\n{p.Text}";
        return text.Length > MaxEmbeddedChars ? text[..MaxEmbeddedChars] : text;
    }
}
