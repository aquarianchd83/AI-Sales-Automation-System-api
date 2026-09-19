namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>Per-1,000-token rates for one conversational AI model, in USD.</summary>
public class AiModelRates
{
    public decimal PromptPer1K { get; set; }

    public decimal CompletionPer1K { get; set; }
}

/// <summary>
/// Bound from "Ai:Pricing". What a conversational AI reply is charged at, used to estimate AI spend from
/// each interaction's model and token counts (see Platform.AiSpendEstimator). List prices copied by hand -
/// the same "estimate, the provider's own bill is the authority" caveat as WhatsAppPricingOptions and
/// LeadDiscoveryPricingOptions.
/// </summary>
public class AiPricingOptions
{
    /// <summary>Keyed as <c>Provider/model</c> - <see cref="KeyFor"/> turns the "Provider:model" an
    /// interaction records into it, because a ':' cannot appear inside a configuration key. Configuration
    /// merges into these defaults by key.</summary>
    public Dictionary<string, AiModelRates> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Anthropic/claude-haiku-4-5"] = new() { PromptPer1K = 0.001m, CompletionPer1K = 0.005m },
        ["Anthropic/claude-sonnet-4-5"] = new() { PromptPer1K = 0.003m, CompletionPer1K = 0.015m },
        ["OpenAI/gpt-5-nano"] = new() { PromptPer1K = 0.00005m, CompletionPer1K = 0.0004m },
        ["OpenAI/gpt-5-mini"] = new() { PromptPer1K = 0.00025m, CompletionPer1K = 0.002m },
        ["OpenAI/gpt-5"] = new() { PromptPer1K = 0.00125m, CompletionPer1K = 0.01m }
    };

    /// <summary>The model the platform runs conversations on, as <c>Provider/model</c> (see <see cref="KeyFor"/>), chosen
    /// on the Configuration page. An interaction whose model has no row of its own is priced at this model's rates.
    /// Blank means no choice was made, and an unlisted model is counted as free.</summary>
    public string? DefaultModel { get; set; }

    public AiModelRates? RatesFor(string modelUsed)
    {
        if (Models.TryGetValue(KeyFor(modelUsed), out var rates))
            return rates;

        // Simulated interactions are deliberately free - never priced at a real model's rates.
        if (modelUsed.StartsWith("Simulated", StringComparison.OrdinalIgnoreCase))
            return null;

        return !string.IsNullOrWhiteSpace(DefaultModel) && Models.TryGetValue(DefaultModel, out var chosen) ? chosen : null;
    }

    public static string KeyFor(string modelUsed) => modelUsed.Replace(':', '/');
}
