using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// Keyword search scored in the application with BM25, over the chunks that pass the hard filter.
///
/// The fallback for a SQL Server without Full-Text Search, and the store that runs under test. It is
/// not a stand-in for correctness - BM25 is precisely what a full-text engine's ranking approximates -
/// but it reads every eligible chunk's SearchText per query, so like the JSON vector store it is
/// correct rather than fast, and is meant for corpora of thousands of chunks, not millions.
///
/// Chosen over EC-23's "vector-only mode" for a concrete reason: on a server with neither native
/// vectors nor Full-Text, vector-only means retrieval that cannot find an error code. A slower keyword
/// leg is better than none.
/// </summary>
public sealed class Bm25KeywordStore : IKeywordSearchStore
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly ApplicationDbContext _context;

    public Bm25KeywordStore(ApplicationDbContext context)
    {
        _context = context;
    }

    public string ProviderName => "InApplicationBm25";

    public async Task<IReadOnlyList<KeywordHit>> SearchAsync(
        string queryText, RetrievalFilter filter, int topN, CancellationToken cancellationToken = default)
    {
        var queryTerms = Tokenize(queryText).Distinct().ToList();
        if (queryTerms.Count == 0 || topN <= 0)
            return Array.Empty<KeywordHit>();

        var docs = await _context.KnowledgeBaseChunks.AsNoTracking()
            .Eligible(filter, matchEmbeddingSpace: false)
            .Select(c => new { c.Id, c.ArticleId, c.SearchText })
            .ToListAsync(cancellationToken);

        if (docs.Count == 0)
            return Array.Empty<KeywordHit>();

        var tokenized = docs.Select(d => (d.Id, d.ArticleId, Terms: Tokenize(d.SearchText).ToList())).ToList();
        var averageLength = tokenized.Average(d => (double)d.Terms.Count);
        if (averageLength <= 0)
            return Array.Empty<KeywordHit>();

        // Document frequency per query term, over the ELIGIBLE set - so a term's rarity is measured
        // against what this tenant can actually see, not the whole platform's corpus.
        var documentFrequency = queryTerms.ToDictionary(t => t, t => tokenized.Count(d => d.Terms.Contains(t)));

        var hits = new List<KeywordHit>();
        foreach (var doc in tokenized)
        {
            double score = 0;
            foreach (var term in queryTerms)
            {
                var tf = doc.Terms.Count(t => t == term);
                if (tf == 0)
                    continue;

                var df = documentFrequency[term];
                var idf = Math.Log(1 + (tokenized.Count - df + 0.5) / (df + 0.5));
                score += idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * doc.Terms.Count / averageLength));
            }

            if (score > 0)
                hits.Add(new KeywordHit(doc.Id, doc.ArticleId, score));
        }

        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.ChunkId).Take(topN).ToList();
    }

    /// <summary>Lowercased letter/digit runs. Unicode-aware, so Devanagari words survive as words
    /// (the SQL Server Hindi word breaker's weakness, §J.6, does not apply here).</summary>
    internal static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var word = new StringBuilder();
        foreach (var ch in text)
        {
            // Combining marks (Devanagari vowel signs) belong to the word they modify.
            if (char.IsLetterOrDigit(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                word.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
            yield return word.ToString();
    }
}

/// <summary>
/// Keyword search through SQL Server Full-Text Search (<c>FREETEXTTABLE</c>), over the same hard
/// filter as every other leg. Used when the server has Full-Text installed and the index exists.
/// </summary>
public sealed class SqlServerFullTextKeywordStore : IKeywordSearchStore
{
    private readonly ApplicationDbContext _context;

    public SqlServerFullTextKeywordStore(ApplicationDbContext context)
    {
        _context = context;
    }

    public string ProviderName => "SqlServerFullText";

    public async Task<IReadOnlyList<KeywordHit>> SearchAsync(
        string queryText, RetrievalFilter filter, int topN, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queryText) || topN <= 0)
            return Array.Empty<KeywordHit>();

        // The keyword leg does not use vectors, so it must not inherit the vector leg's
        // same-embedding-space restriction.
        var keywordFilter = filter with { EmbeddingProvider = null, EmbeddingModel = null };

        var sql = $@"
WITH EligibleChunks AS ({EligibleChunkQuery.Cte})
SELECT TOP ({topN.ToString(CultureInfo.InvariantCulture)}) e.Id, e.ArticleId, ft.[RANK] AS Score
FROM   EligibleChunks e
JOIN   FREETEXTTABLE(KnowledgeBaseChunks, SearchText, @QueryText, {topN.ToString(CultureInfo.InvariantCulture)}) ft
       ON ft.[KEY] = e.Id
ORDER  BY ft.[RANK] DESC";

        var connection = _context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in EligibleChunkQuery.Parameters(keywordFilter))
            command.Parameters.Add(parameter);
        command.Parameters.Add(new SqlParameter("@QueryText", queryText));

        var hits = new List<KeywordHit>();
        await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                hits.Add(new KeywordHit(reader.GetGuid(0), reader.GetGuid(1), Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture)));
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }

        return hits;
    }
}
