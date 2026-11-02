using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Infrastructure.Ai;

namespace WhatsAppSalesAutomation.Infrastructure.Integrations;

/// <summary>Reads the policy settings and each provider's prices at request time, so a change on the Integrations page applies from the next AI task.</summary>
public sealed class AiTaskPolicyProvider : IAiTaskPolicyProvider
{
    private readonly IOptionsSnapshot<AiFeatureOptions> _options;
    private readonly IOptionsSnapshot<AiProviderSettings> _providers;
    private readonly IOptionsSnapshot<AiPricingOptions>? _modelPrices;

    public AiTaskPolicyProvider(
        IOptionsSnapshot<AiFeatureOptions> options, IOptionsSnapshot<AiProviderSettings> providers, IOptionsSnapshot<AiPricingOptions>? modelPrices = null)
    {
        _options = options;
        _providers = providers;
        _modelPrices = modelPrices;
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

        var providers = _providers.Value;
        return new AiTaskPolicy(
            p.FallbackEnabled, order, scenario,
            Math.Max(0m, p.MaxCostPerCallUsd), Math.Clamp(p.TimeoutSeconds, 5, 300), Math.Clamp(p.MaxRetriesPerProvider, 0, 3), Math.Clamp(p.RetryDelayMs, 0, 10_000),
            new Dictionary<string, ProviderPrice>(StringComparer.OrdinalIgnoreCase)
            {
                ["OpenAI"] = PriceOf("OpenAI", providers.OpenAI.ChatModel, providers.OpenAI.InputPer1MUsd, providers.OpenAI.OutputPer1MUsd),
                ["Anthropic"] = PriceOf("Anthropic", providers.Anthropic.Model, providers.Anthropic.InputPer1MUsd, providers.Anthropic.OutputPer1MUsd),
                ["Google"] = PriceOf("Google", providers.Google.ChatModel, providers.Google.InputPer1MUsd, providers.Google.OutputPer1MUsd),
            });
    }

    /// <summary>One price list: the model's row on the Configuration page's "Model token prices" (per 1,000 tokens, shown here per million).
    /// Only a model with no row there falls back to the price typed beside the provider's key.</summary>
    private ProviderPrice PriceOf(string provider, string? model, decimal legacyInputPer1M, decimal legacyOutputPer1M)
    {
        var rates = _modelPrices?.Value.RatesForConfiguredModel(provider, model);
        return rates is null
            ? new ProviderPrice(legacyInputPer1M, legacyOutputPer1M)
            : new ProviderPrice(rates.PromptPer1K * 1000m, rates.CompletionPer1K * 1000m);
    }
}
