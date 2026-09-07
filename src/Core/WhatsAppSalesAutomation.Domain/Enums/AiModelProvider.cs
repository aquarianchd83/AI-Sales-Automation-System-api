namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Which AI chat model a KnowledgeBaseArticle has been explicitly published to - see
/// KnowledgeBaseArticleModelPublication. Deliberately mirrors the three real (non-"Simulated")
/// values of AiProviderSettings.Provider in the Infrastructure project ("OpenAI", "Google",
/// "Anthropic") so a config string round-trips via Enum.Parse without a translation table.
/// "Simulated" has no entry here - it's a local-dev chat-client fallback, not a publishable
/// target, and KnowledgeBaseService.RetrieveRelevantChunksAsync skips the model filter entirely
/// when the active provider is Simulated.</summary>
public enum AiModelProvider
{
    OpenAI = 0,
    Google = 1,
    Anthropic = 2
}
