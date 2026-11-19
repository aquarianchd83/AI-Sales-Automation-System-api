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
    string GoogleBaseUrl,
    bool AnthropicEnabled = true,
    bool OpenAiEnabled = true,
    bool GoogleEnabled = true,
    IReadOnlyDictionary<string, string>? TaskModels = null)
{
    /// <summary>The same credentials with <paramref name="group"/>'s own models in place of the chat models: one model per provider, chosen
    /// under "Model by task" on the AI Providers page, keyed "{group}/{provider}". A provider the group has no model for keeps its chat model, and
    /// a null group (a task that is in none) changes nothing.</summary>
    public AiCredentials ForGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group) || TaskModels is null || TaskModels.Count == 0)
            return this;

        string? Chosen(string provider) =>
            TaskModels.TryGetValue($"{group}/{provider}", out var model) && !string.IsNullOrWhiteSpace(model) ? model.Trim() : null;

        return this with
        {
            AnthropicModel = Chosen("Anthropic") ?? AnthropicModel,
            OpenAiChatModel = Chosen("OpenAI") ?? OpenAiChatModel,
            GoogleChatModel = Chosen("Google") ?? GoogleChatModel,
        };
    }

    /// <summary>Whether the platform admin has this provider switched on. A provider that is off has no key in this record.</summary>
    public bool IsEnabled(string provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "anthropic" => AnthropicEnabled,
        "openai" => OpenAiEnabled,
        "google" => GoogleEnabled,
        _ => true,
    };
}
