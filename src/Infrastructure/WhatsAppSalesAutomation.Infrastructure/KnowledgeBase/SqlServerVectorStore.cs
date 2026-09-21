using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// The native path: similarity is computed by SQL Server's <c>VECTOR_DISTANCE</c> over a
/// <c>VECTOR(1536)</c> column, so the database returns the top N and nothing else crosses the wire.
///
/// The vector column is deliberately NOT part of the EF model. EF Core 8 has no mapping for the
/// VECTOR type, and more importantly the column only exists on servers that support it - the
/// migration adds it through dynamic SQL guarded by a capability check, so a pre-2025 server simply
/// does not have it. Keeping it out of the model means the model is identical on both, and only this
/// class - which never runs on a server without the column - knows the column is there.
/// </summary>
public sealed class SqlServerVectorStore : IVectorStore
{
    /// <summary>The column added by the Phase 6 migration where the server supports it. Named here
    /// once because three statements below reference it.</summary>
    public const string VectorColumn = "EmbeddingVector";

    private readonly ApplicationDbContext _context;

    public SqlServerVectorStore(ApplicationDbContext context)
    {
        _context = context;
    }

    public string ProviderName => "SqlServerNativeVector";

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        RetrievalFilter filter,
        int topN,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.IsEmpty || topN <= 0)
            return Array.Empty<VectorHit>();

        // TOP is interpolated, not parameterized: SQL Server will not accept a parameter in a TOP
        // clause without a subquery, and topN is an int from configuration, never from a caller.
        var sql = $@"
WITH EligibleChunks AS ({EligibleChunkQuery.Cte}
      AND  c.{VectorColumn} IS NOT NULL)
SELECT TOP ({topN.ToString(CultureInfo.InvariantCulture)})
       e.Id, e.ArticleId,
       1 - VECTOR_DISTANCE('cosine', k.{VectorColumn}, CAST(@QueryVector AS VECTOR({queryVector.Length}))) AS Similarity
FROM   EligibleChunks e
JOIN   KnowledgeBaseChunks k ON k.Id = e.Id
ORDER  BY VECTOR_DISTANCE('cosine', k.{VectorColumn}, CAST(@QueryVector AS VECTOR({queryVector.Length})))";

        var parameters = new List<SqlParameter>(EligibleChunkQuery.Parameters(filter))
        {
            new("@QueryVector", ToVectorLiteral(queryVector))
        };

        var hits = new List<VectorHit>();

        var connection = _context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter);

        await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new VectorHit(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    // VECTOR_DISTANCE returns cosine DISTANCE; the query already converts it to
                    // similarity. Doing that conversion in SQL rather than here keeps ORDER BY and
                    // the returned score derived from the same expression.
                    reader.GetDouble(2)));
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }

        return hits;
    }

    public async Task UpsertAsync(IReadOnlyList<ChunkVector> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
            return;

        // One statement per chunk rather than a table-valued parameter. Ingestion writes a few dozen
        // chunks per article inside a transaction the caller owns, so the round trips are bounded and
        // a TVP carrying a VECTOR column would need a server-side type that the fallback path could
        // not create. Revisit if bulk re-index of the whole corpus becomes the common case.
        foreach (var chunk in chunks)
        {
            var sql = $@"
UPDATE KnowledgeBaseChunks
SET    {VectorColumn} = CAST(@Vector AS VECTOR({chunk.Embedding.Length})),
       Embedding = @Json,
       EmbeddingProvider = @Provider,
       EmbeddingModel = @Model,
       EmbeddingDimensions = @Dimensions
WHERE  Id = @ChunkId";

            await _context.Database.ExecuteSqlRawAsync(
                sql,
                new object[]
                {
                    new SqlParameter("@Vector", ToVectorLiteral(chunk.Embedding)),
                    // The JSON column is written too, not instead. It is what the fallback store
                    // reads, what a human inspects, and what a future re-index reads if the vector
                    // column ever has to be rebuilt - losing it would make the native column the only
                    // copy of data that cost money to produce.
                    new SqlParameter("@Json", ToVectorLiteral(chunk.Embedding)),
                    new SqlParameter("@Provider", chunk.Provider),
                    new SqlParameter("@Model", chunk.Model),
                    new SqlParameter("@Dimensions", chunk.Embedding.Length),
                    new SqlParameter("@ChunkId", chunk.ChunkId)
                },
                cancellationToken);
        }
    }

    public Task DeleteByArticleAsync(Guid articleId, CancellationToken cancellationToken = default) =>
        _context.Database.ExecuteSqlRawAsync(
            $"UPDATE KnowledgeBaseChunks SET {VectorColumn} = NULL, Embedding = NULL WHERE ArticleId = @ArticleId",
            new object[] { new SqlParameter("@ArticleId", articleId) },
            cancellationToken);

    /// <summary>
    /// "[0.1,-0.2,0.3]" - the JSON array form SQL Server casts to VECTOR, and the same text the JSON
    /// column holds, so one representation serves both stores.
    ///
    /// "R" round-trip formatting with the invariant culture is not fussiness: under a culture that
    /// uses a comma as the decimal separator, the default formatting would produce "[0,1,-0,2]",
    /// which is a valid JSON array of twice the length and entirely the wrong vector.
    /// </summary>
    public static string ToVectorLiteral(ReadOnlyMemory<float> vector)
    {
        var span = vector.Span;
        var builder = new StringBuilder(span.Length * 12 + 2);
        builder.Append('[');

        for (var i = 0; i < span.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(span[i].ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }
}
