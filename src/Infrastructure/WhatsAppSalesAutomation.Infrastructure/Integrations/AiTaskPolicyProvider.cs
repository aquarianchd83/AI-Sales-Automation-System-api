using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Infrastructure.Ai;

namespace WhatsAppSalesAutomation.Infrastructure.Integrations;

/// <summary>Reads the policy settings and each provider's prices at request time, so a change on the Integrations page applies from the next AI task.</summary>
public sealed class AiTaskPolicyProvider : IAiTaskPolicyProvider
{
    private readonly IOptionsSnapshot<AiFeatureOptions> _options;
    private readonly IOptionsSnapshot<AiProviderSettings> _providers;

    public AiTaskPolicyProvider(IOptionsSnapshot<AiFeatureOptions> options, IOptionsSnapshot<AiProviderSettings> providers)
    {
        _options = options;
        _providers = providers;
    }

    public AiTaskPolicy Get()
    {
        var o = _options.Value;
        var p = o.Policy;

        var order = (p.FallbackOrder ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => AiCapabilities.RealProviders.FirstOrDefault(r => string.Equals(r, x, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct()
            .ToList();

        var scenario = AiSimulatorScenarios.All.FirstOrDefault(s => string.Equals(s, p.SimulatorScenario?.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? AiSimulatorScenarios.Success;

        return new AiTaskPolicy(
            p.FallbackEnabled, order, scenario, p.ScannedDocumentsViaAi,
            Math.Max(0m, p.MaxCostPerCallUsd), Math.Clamp(p.TimeoutSeconds, 5, 300), Math.Clamp(p.MaxRetriesPerProvider, 0, 3), Math.Clamp(p.RetryDelayMs, 0, 10_000),
            new Dictionary<string, ProviderPrice>(StringComparer.OrdinalIgnoreCase)
            {
                // Prices are each provider's own settings on the AI Providers screen, next to its key and model.
                ["OpenAI"] = new(_providers.Value.OpenAI.InputPer1MUsd, _providers.Value.OpenAI.OutputPer1MUsd),
                ["Anthropic"] = new(_providers.Value.Anthropic.InputPer1MUsd, _providers.Value.Anthropic.OutputPer1MUsd),
                ["Google"] = new(_providers.Value.Google.InputPer1MUsd, _providers.Value.Google.OutputPer1MUsd),
            });
    }
}
