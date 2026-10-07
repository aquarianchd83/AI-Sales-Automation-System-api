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
        return new AiCredentials(
            s.Provider,
            s.EmbeddingProvider,
            Blank(s.Anthropic.ApiKey), s.Anthropic.Model, s.Anthropic.ApiVersion, s.Anthropic.BaseUrl,
            Blank(s.OpenAI.ApiKey), s.OpenAI.ChatModel, s.OpenAI.EmbeddingModel, s.OpenAI.BaseUrl,
            Blank(s.Google.ApiKey), s.Google.ChatModel, s.Google.EmbeddingModel, s.Google.BaseUrl);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
