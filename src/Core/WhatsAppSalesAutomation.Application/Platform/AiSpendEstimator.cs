using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Options;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Turns an AI interaction's model + token counts into an estimated USD cost.</summary>
public interface IAiSpendEstimator
{
    decimal EstimateUsd(string? modelUsed, int? promptTokens, int? completionTokens);
}

/// <summary>
/// Approximates AI spend from <c>AiInteraction.ModelUsed</c> + token counts - there is no real
/// $-cost tracking anywhere in this codebase (AiInteraction only stores PromptTokens/CompletionTokens/
/// ModelUsed, never a price), so every figure this produces is an ESTIMATE against a price list, not a
/// reconciled bill. The rates are <see cref="AiPricingOptions"/> ("Ai:Pricing"), edited from the Platform
/// Admin Console's Configuration page, and will drift out of date when a provider changes pricing. An
/// unrecognized <c>ModelUsed</c> value (including every "Simulated:..." value used by tests/local dev) is
/// priced at $0 rather than guessed.
/// </summary>
public class AiSpendEstimator : IAiSpendEstimator
{
    private readonly AiPricingOptions _pricing;

    public AiSpendEstimator(IOptionsSnapshot<AiPricingOptions> pricing)
    {
        _pricing = pricing.Value;
    }

    /// <summary>Rounded to 4 decimal places - this is a dashboard estimate, not an invoice line, and
    /// summing many rounded-to-the-cent values would compound error worse than keeping a little extra
    /// precision through the sum.</summary>
    public decimal EstimateUsd(string? modelUsed, int? promptTokens, int? completionTokens)
    {
        if (modelUsed is null || _pricing.RatesFor(modelUsed) is not { } rates)
            return 0m;

        var promptCost = (promptTokens ?? 0) / 1000m * rates.PromptPer1K;
        var completionCost = (completionTokens ?? 0) / 1000m * rates.CompletionPer1K;
        return Math.Round(promptCost + completionCost, 4);
    }
}
