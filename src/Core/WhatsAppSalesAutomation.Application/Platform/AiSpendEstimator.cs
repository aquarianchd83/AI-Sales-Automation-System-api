namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// Approximates AI spend from <c>AiInteraction.ModelUsed</c> + token counts - there is no real
/// $-cost tracking anywhere in this codebase (AiInteraction only stores PromptTokens/CompletionTokens/
/// ModelUsed, never a price), so every figure this produces is an ESTIMATE against a hand-maintained
/// price list, not a reconciled bill. Rates are per 1,000 tokens, in USD, and will drift out of date -
/// same "pricing changes and should be re-checked periodically" caveat this codebase already applies
/// to <c>AiProviderSettings</c>. Update <see cref="RatesPer1KTokensUsd"/> when a provider changes
/// pricing; an unrecognized <c>ModelUsed</c> value (including every "Simulated:..." value used by
/// tests/local dev) is priced at $0 rather than guessed.
/// </summary>
public static class AiSpendEstimator
{
    private static readonly IReadOnlyDictionary<string, (decimal PromptPer1K, decimal CompletionPer1K)> RatesPer1KTokensUsd =
        new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            // Rates as of the rows this table was written against - re-check before trusting these
            // for anything beyond a rough dashboard number.
            ["Anthropic:claude-haiku-4-5"] = (0.001m, 0.005m),
            ["Anthropic:claude-sonnet-4-5"] = (0.003m, 0.015m),
            ["OpenAI:gpt-5-nano"] = (0.00005m, 0.0004m),
            ["OpenAI:gpt-5-mini"] = (0.00025m, 0.002m),
            ["OpenAI:gpt-5"] = (0.00125m, 0.01m),
        };

    /// <summary>Rounded to 4 decimal places - this is a dashboard estimate, not an invoice line, and
    /// summing many rounded-to-the-cent values would compound error worse than keeping a little extra
    /// precision through the sum.</summary>
    public static decimal EstimateUsd(string? modelUsed, int? promptTokens, int? completionTokens)
    {
        if (modelUsed is null || !RatesPer1KTokensUsd.TryGetValue(modelUsed, out var rates))
            return 0m;

        var promptCost = (promptTokens ?? 0) / 1000m * rates.PromptPer1K;
        var completionCost = (completionTokens ?? 0) / 1000m * rates.CompletionPer1K;
        return Math.Round(promptCost + completionCost, 4);
    }
}
