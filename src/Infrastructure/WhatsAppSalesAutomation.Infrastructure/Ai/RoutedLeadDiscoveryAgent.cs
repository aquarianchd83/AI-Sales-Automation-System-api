using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// The discovery agent the run service talks to: it hands each run to whichever engine the platform admin picked for
/// "Discovery lead" on the Integrations page - the simulator, ChatGPT, Claude or Gemini - read per run, so a save there is live
/// with no restart. The engines themselves are <see cref="SimulatedLeadDiscoveryAgent"/>, <see cref="OpenAiLeadDiscoveryAgent"/>
/// , <see cref="AnthropicLeadDiscoveryAgent"/> and <see cref="GeminiLeadDiscoveryAgent"/>; the real ones are wrapped so every call lands in the AI call log under its own provider.
/// </summary>
public sealed class RoutedLeadDiscoveryAgent : ILeadDiscoveryAgent
{
    private readonly IAiFeatureRouting _routing;
    private readonly SimulatedLeadDiscoveryAgent _simulated;
    private readonly OpenAiLeadDiscoveryAgent _openAi;
    private readonly AnthropicLeadDiscoveryAgent _anthropic;
    private readonly GeminiLeadDiscoveryAgent _gemini;
    private readonly AiCallRecorder _recorder;

    public RoutedLeadDiscoveryAgent(
        IAiFeatureRouting routing,
        SimulatedLeadDiscoveryAgent simulated,
        OpenAiLeadDiscoveryAgent openAi,
        AnthropicLeadDiscoveryAgent anthropic,
        GeminiLeadDiscoveryAgent gemini,
        AiCallRecorder recorder)
    {
        _routing = routing;
        _simulated = simulated;
        _openAi = openAi;
        _anthropic = anthropic;
        _gemini = gemini;
        _recorder = recorder;
    }

    public Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default)
    {
        var choice = _routing.Resolve(AiFeatures.DiscoveryLead);

        ILeadDiscoveryAgent engine = !choice.UsesThirdParty
            ? _simulated
            : choice.Provider switch
            {
                "OpenAI" => new RecordingLeadDiscoveryAgent(_openAi, _recorder, "OpenAI"),
                "Anthropic" => new RecordingLeadDiscoveryAgent(_anthropic, _recorder, "Anthropic"),
                "Google" => new RecordingLeadDiscoveryAgent(_gemini, _recorder, "Google"),
                // A tool discovery cannot run on is never called: the simulator answers (AiFeatureRouting already guarantees this).
                _ => _simulated,
            };

        return engine.DiscoverAsync(request, cancellationToken);
    }
}
