namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Every embedding provider this deployment could call - Simulated, OpenAI, Google - regardless of
/// which one is currently "active" via AiProviders:EmbeddingProvider. IEmbeddingService (injected
/// directly) resolves to exactly one of these, chosen once at startup; this catalog exists so
/// KnowledgeBaseService.ReembedAsync can additionally embed each chunk with every OTHER configured
/// provider too, and record all of them (see KnowledgeBaseChunkEmbedding) - not just whichever one
/// retrieval happens to be using right now. Query-time retrieval (RetrieveRelevantChunksAsync) still
/// only ever needs the single active IEmbeddingService, so that injection is unchanged.
/// </summary>
public interface IEmbeddingProviderCatalog
{
    IReadOnlyList<IEmbeddingService> AllProviders { get; }
}
