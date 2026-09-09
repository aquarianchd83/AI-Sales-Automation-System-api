using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>Thin IOptions wrapper - see IActiveAiProviderAccessor's doc comment for why this
/// exists instead of Application referencing AiProviderSettings directly.</summary>
public class ActiveAiProviderAccessor : IActiveAiProviderAccessor
{
    private readonly AiProviderSettings _settings;

    public ActiveAiProviderAccessor(IOptionsSnapshot<AiProviderSettings> settings)
    {
        _settings = settings.Value;
    }

    public string ActiveProvider => _settings.Provider;

    public bool HasApiKey(string provider) => provider?.ToLowerInvariant() switch
    {
        "anthropic" => !string.IsNullOrWhiteSpace(_settings.Anthropic.ApiKey),
        "openai" => !string.IsNullOrWhiteSpace(_settings.OpenAI.ApiKey),
        "google" => !string.IsNullOrWhiteSpace(_settings.Google.ApiKey),
        _ => false
    };
}
