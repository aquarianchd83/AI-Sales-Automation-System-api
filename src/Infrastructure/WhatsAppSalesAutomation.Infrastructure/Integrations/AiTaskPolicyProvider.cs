using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai.Tasks;

namespace WhatsAppSalesAutomation.Infrastructure.Integrations;

/// <summary>Reads the policy and price settings at request time, so a change on the Integrations page applies from the next AI task.</summary>
public sealed class AiTaskPolicyProvider : IAiTaskPolicyProvider
{
    private readonly IOptionsSnapshot<AiFeatureOptions> _options;

    public AiTaskPolicyProvider(IOptionsSnapshot<AiFeatureOptions> options) => _options = options;

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
                ["OpenAI"] = new(o.Pricing.OpenAI.InputPer1MUsd, o.Pricing.OpenAI.OutputPer1MUsd),
                ["Anthropic"] = new(o.Pricing.Anthropic.InputPer1MUsd, o.Pricing.Anthropic.OutputPer1MUsd),
                ["Google"] = new(o.Pricing.Google.InputPer1MUsd, o.Pricing.Google.OutputPer1MUsd),
            });
    }
}
