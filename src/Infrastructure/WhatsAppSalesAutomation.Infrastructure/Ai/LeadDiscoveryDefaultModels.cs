using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>Reads the discovery default models from <see cref="LeadDiscoveryAgentSettings"/>, so the AI Providers page shows what a run really uses.</summary>
public sealed class LeadDiscoveryDefaultModels : ILeadDiscoveryDefaultModels
{
    private readonly IOptionsSnapshot<LeadDiscoveryAgentSettings> _settings;

    public LeadDiscoveryDefaultModels(IOptionsSnapshot<LeadDiscoveryAgentSettings> settings) => _settings = settings;

    public string ModelFor(string provider)
    {
        var s = _settings.Value;
        return provider switch
        {
            "Anthropic" => s.Model,
            "OpenAI" => string.IsNullOrWhiteSpace(s.OpenAiModel) ? "gpt-5-mini" : s.OpenAiModel,
            _ => string.IsNullOrWhiteSpace(s.GeminiModel) ? "gemini-2.5-flash" : s.GeminiModel,
        };
    }
}
