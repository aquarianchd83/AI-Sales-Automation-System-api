namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Exposes the currently-configured chat AI provider (Infrastructure's AiProviderSettings.Provider
/// - "Simulated"/"Anthropic"/"OpenAI"/"Google") to the Application layer without Application taking
/// a dependency on Infrastructure (which owns AiProviderSettings because it also holds API keys).
/// Same layering-bridge pattern as ICurrentUserService/IDateTimeProvider. Used by
/// KnowledgeBaseService.RetrieveRelevantChunksAsync to filter retrieval to articles published for
/// whichever model is actually answering.
/// </summary>
public interface IActiveAiProviderAccessor
{
    string ActiveProvider { get; }

    /// <summary>Whether AiProviderSettings has an API key configured for the named chat provider -
    /// "Anthropic"/"OpenAI"/"Google" (case-insensitive; false for "Simulated" and anything
    /// unrecognized - Simulated needs no key but is also not a publishable AiModelProvider target, see
    /// its own enum doc comment). Used by KnowledgeBaseService.PublishToModelAsync to refuse making an
    /// article eligible for a chat model that could never actually answer with it.</summary>
    bool HasApiKey(string provider);
}
