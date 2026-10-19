using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>Reads the platform-wide "AiProviders" settings through <see cref="IOptionsSnapshot{T}"/>, so a
/// value a PlatformSuperAdmin saves is live on the next request with no restart.</summary>
public class PlatformAiConfigProvider : IPlatformAiConfigProvider
{
    private readonly IOptionsSnapshot<AiProviderSettings> _settings;

    public PlatformAiConfigProvider(IOptionsSnapshot<AiProviderSettings> settings)
    {
        _settings = settings;
    }

    public AiCredentials Get()
    {
        var s = _settings.Value;
        // The one place the On/Off switch is enforced: an Off provider has no key and is never the chosen chat or embedding provider,
        // so every caller (chat, embeddings, helper text, feature settings) behaves as if it had never been set up.
        string Choice(string? chosen) => IsOff(s, chosen) ? "Simulated" : chosen ?? "Simulated";
        return new AiCredentials(
            Choice(s.Provider),
            Choice(s.EmbeddingProvider),
            s.Anthropic.Enabled ? Blank(s.Anthropic.ApiKey) : null, s.Anthropic.Model, s.Anthropic.ApiVersion, s.Anthropic.BaseUrl,
            s.OpenAI.Enabled ? Blank(s.OpenAI.ApiKey) : null, s.OpenAI.ChatModel, s.OpenAI.EmbeddingModel, s.OpenAI.BaseUrl,
            s.Google.Enabled ? Blank(s.Google.ApiKey) : null, s.Google.ChatModel, s.Google.EmbeddingModel, s.Google.BaseUrl,
            s.Anthropic.Enabled, s.OpenAI.Enabled, s.Google.Enabled);
    }

    private static bool IsOff(AiProviderSettings s, string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "anthropic" => !s.Anthropic.Enabled,
        "openai" => !s.OpenAI.Enabled,
        "google" => !s.Google.Enabled,
        _ => false,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
