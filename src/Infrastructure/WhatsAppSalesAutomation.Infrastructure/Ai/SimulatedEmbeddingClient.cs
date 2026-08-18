using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Stands in for a real embeddings provider when none is configured
/// (<c>AiProviders:EmbeddingProvider = "Simulated"</c>, the default) - a deterministic bag-of-words
/// "hashing trick" embedding (each word hashes into one of <see cref="Dimensions"/> buckets, L2
/// normalized), not a real semantic embedding. Good enough for local testing that RAG retrieval
/// prefers a knowledge base chunk sharing vocabulary with the query over an unrelated one; it will not
/// capture synonyms or meaning the way a real model does.
///
/// Uses a hand-rolled FNV-1a hash rather than <see cref="string.GetHashCode()"/> deliberately: .NET
/// randomizes GetHashCode's seed per process for security, so the same word would hash to a different
/// bucket after every app restart - silently corrupting the meaning of every embedding already stored
/// in KnowledgeBaseChunks.Embedding from before that restart. FNV-1a is stable across processes and
/// machines, which persisted embeddings require.
/// </summary>
public class SimulatedEmbeddingClient : IEmbeddingService
{
    private const int Dimensions = 64;
    private static readonly char[] Delimiters = { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'' };

    public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var vector = new float[Dimensions];

        foreach (var word in Tokenize(text))
        {
            var index = (int)(StableHash(word) % Dimensions);
            vector[index] += 1f;
        }

        Normalize(vector);
        return Task.FromResult(vector);
    }

    private static IEnumerable<string> Tokenize(string text) =>
        text.ToLowerInvariant().Split(Delimiters, StringSplitOptions.RemoveEmptyEntries);

    private static uint StableHash(string value)
    {
        unchecked
        {
            const uint fnvOffsetBasis = 2166136261;
            const uint fnvPrime = 16777619;

            var hash = fnvOffsetBasis;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return hash;
        }
    }

    private static void Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude <= 0)
            return;

        for (var i = 0; i < vector.Length; i++)
            vector[i] /= magnitude;
    }
}
