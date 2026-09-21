using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// The fallback path for a SQL Server without the native VECTOR type: eligible chunks' JSON
/// embeddings are read and scored in the application.
///
/// This is the pre-Phase-6 approach kept alive behind the new interface, and its limits are real -
/// it transfers every eligible chunk's vector on every query, so cost grows with corpus size rather
/// than with result count. That is why <see cref="SqlServerVectorCapability"/> logs a warning when
/// this store is selected. It is correct, not fast, and it exists so the application runs on a
/// developer machine and a CI runner without a SQL Server 2025 licence.
///
/// The hard metadata filter is applied in the database, not here. Reading everything and filtering
/// in memory would produce the same answers while pulling other tenants' chunks into this process -
/// which §P treats as a breach regardless of what happens to them afterwards.
/// </summary>
public sealed class JsonColumnVectorStore : IVectorStore
{
    private readonly ApplicationDbContext _context;

    public JsonColumnVectorStore(ApplicationDbContext context)
    {
        _context = context;
    }

    public string ProviderName => "JsonColumnCosine";

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        RetrievalFilter filter,
        int topN,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.IsEmpty || topN <= 0)
            return Array.Empty<VectorHit>();

        // Composed with LINQ rather than the shared raw-SQL CTE, because this store also has to run
        // on SQLite under test. EligibleChunkLinq is the single LINQ definition of the hard filter,
        // shared with the keyword store - see its own comment for why it is not written out per store.
        var query = _context.KnowledgeBaseChunks
            .AsNoTracking()
            .Eligible(filter, matchEmbeddingSpace: true)
            .Where(c => c.Embedding != null);

        var candidates = await query
            .Select(c => new { c.Id, c.ArticleId, c.Embedding })
            .ToListAsync(cancellationToken);

        var queryArray = queryVector.ToArray();

        return candidates
            .Select(c => new VectorHit(c.Id, c.ArticleId, CosineSimilarity(queryArray, Deserialize(c.Embedding!))))
            .OrderByDescending(hit => hit.Similarity)
            .Take(topN)
            .ToList();
    }

    public async Task UpsertAsync(IReadOnlyList<ChunkVector> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
            return;

        var ids = chunks.Select(c => c.ChunkId).ToList();
        var tracked = await _context.KnowledgeBaseChunks
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        foreach (var chunk in chunks)
        {
            if (!tracked.TryGetValue(chunk.ChunkId, out var entity))
                continue;   // Chunk deleted between embedding and write - the re-index will redo it.

            entity.Embedding = SqlServerVectorStore.ToVectorLiteral(chunk.Embedding);
            entity.EmbeddingProvider = chunk.Provider;
            entity.EmbeddingModel = chunk.Model;
            entity.EmbeddingDimensions = chunk.Embedding.Length;
        }

        // No SaveChanges here. Ingestion owns the transaction that makes the staging swap atomic, and
        // a save inside this method would commit half of it - see the ingestion pipeline's own
        // transaction handling, and the same split used by KnowledgeBaseService.ReembedAsync.
    }

    public async Task DeleteByArticleAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        var chunks = await _context.KnowledgeBaseChunks
            .Where(c => c.ArticleId == articleId)
            .ToListAsync(cancellationToken);

        foreach (var chunk in chunks)
        {
            chunk.Embedding = null;
            chunk.EmbeddingProvider = null;
            chunk.EmbeddingModel = null;
            chunk.EmbeddingDimensions = null;
        }
    }

    private static float[] Deserialize(string json) =>
        JsonSerializer.Deserialize<float[]>(json) ?? Array.Empty<float>();

    /// <summary>Returns 0 for empty or mismatched-length vectors rather than throwing. A single chunk
    /// embedded under a different model (different dimensionality) should drop out of the ranking,
    /// not take the whole retrieval down with it - the re-index that fixes it is a separate concern
    /// from the query that tripped over it.</summary>
    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
            return 0;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
