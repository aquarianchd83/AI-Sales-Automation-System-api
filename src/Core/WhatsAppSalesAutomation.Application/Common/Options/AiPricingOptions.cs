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

    public AiModelRates? RatesFor(string modelUsed)
    {
        if (Models.TryGetValue(KeyFor(modelUsed), out var rates))
            return rates;

        // Simulated interactions are deliberately free - never priced at a real model's rates.
        if (modelUsed.StartsWith("Simulated", StringComparison.OrdinalIgnoreCase))
            return null;

        // A dated model id ("claude-haiku-4-5-20251001") is read from its undated row. A model with no row at all is counted as free.
        var separator = modelUsed.IndexOf(':');
        return separator > 0 ? RatesForConfiguredModel(modelUsed[..separator], modelUsed[(separator + 1)..]) : null;
    }

    /// <summary>The row for the model a provider is configured to run, or null when it has none. Unlike <see cref="RatesFor"/> it never falls back to the
    /// default model, which belongs to a different provider's call. A dated model id ("claude-haiku-4-5-20251001") matches its row ("claude-haiku-4-5").</summary>
    public AiModelRates? RatesForConfiguredModel(string provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        if (Models.TryGetValue($"{provider}/{model}", out var exact))
            return exact;

        var prefix = provider + "/";
        return Models
            .Where(m => m.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && model.StartsWith(m.Key[prefix.Length..], StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Key.Length)
            .Select(m => m.Value)
            .FirstOrDefault();
    }

    public static string KeyFor(string modelUsed) => modelUsed.Replace(':', '/');
}
