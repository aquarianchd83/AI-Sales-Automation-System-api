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
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default);
}
