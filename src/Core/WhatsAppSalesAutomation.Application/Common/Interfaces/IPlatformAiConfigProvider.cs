namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// The one AI provider configuration, owned by the platform. Tenants never hold, see or choose a
/// provider, model or key: every AI call - chat, embeddings, helper text - resolves its credentials
/// here, whichever tenant (if any) is in scope. The operator edits the values on the Configuration
/// screen (the "AiProviders" settings) and they take effect on the next request, so changing provider
/// or model needs no change on any tenant.
/// </summary>
public interface IPlatformAiConfigProvider
{
    /// <summary>The platform's current, already-decrypted config. Never logged or returned from any endpoint.</summary>
    AiCredentials Get();
}

/// <summary>Provider choice, models and keys ready to hand to whichever provider's client.</summary>
public record AiCredentials(
    string Provider,
    string EmbeddingProvider,
    string? AnthropicApiKey,
    string AnthropicModel,
    string AnthropicApiVersion,
    string AnthropicBaseUrl,
    string? OpenAiApiKey,
    string OpenAiChatModel,
    string OpenAiEmbeddingModel,
    string OpenAiBaseUrl,
    string? GoogleApiKey,
    string GoogleChatModel,
    string GoogleEmbeddingModel,
    string GoogleBaseUrl);
