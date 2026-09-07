namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Turns text into a vector for RAG retrieval. Kept separate from <see cref="IAiService"/> rather than
/// folded into it because the two are chosen independently in this codebase: Anthropic's Messages API
/// has no embeddings endpoint, so a deployment can run Claude for chat while an OpenAI or Google client
/// (selected via <c>Ai:EmbeddingProvider</c>, independent of <c>Ai:Provider</c>) handles embeddings, or
/// Simulated for local dev without any API key.
///
/// This phase's retrieval is in-application cosine similarity (fetch candidate
/// KnowledgeBaseChunk.Embedding values, compare in the Application layer) rather than a SQL Server
/// native vector index or an external vector DB - see KnowledgeBaseChunk's doc comment. That choice
/// lives entirely on the consumer side (KnowledgeBaseService); this interface only produces vectors,
/// it does not care how they end up compared.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Which provider this implementation is - "Simulated"/"OpenAI"/"Google", matching
    /// AiProviderSettings.EmbeddingProvider's own values. KnowledgeBaseService stamps this onto every
    /// KnowledgeBaseChunk it embeds, so a chunk records which provider actually produced its current
    /// vector - visible in the admin UI as the thing that was previously invisible: a chunk embedded
    /// while EmbeddingProvider was "Simulated" looks identical to one embedded via "OpenAI" until you
    /// check this.</summary>
    string ProviderName { get; }

    /// <summary>The specific model within ProviderName - e.g. "text-embedding-3-small" for OpenAI,
    /// "text-embedding-004" for Google, "hashing-trick-64d" for Simulated. Stored alongside
    /// ProviderName on the chunk for the same reason.</summary>
    string ModelName { get; }

    /// <summary>Whether this provider actually has what it needs to be called - always true for
    /// Simulated (no credentials needed), true for OpenAI/Google only when their ApiKey is configured.
    /// IEmbeddingProviderCatalog.AllProviders uses this to skip a provider that would only ever fail
    /// (and log a warning) rather than attempt it on every single publish.</summary>
    bool IsAvailable { get; }

    Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default);
}
