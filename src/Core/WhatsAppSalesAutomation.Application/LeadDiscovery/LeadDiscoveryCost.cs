using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>
/// Turns the usage an agent reports into money. Pure arithmetic over
/// <see cref="LeadDiscoveryPricingOptions"/>, so the run service can price a run without knowing which
/// provider ran it, and the same figure can be checked in tests.
/// </summary>
public static class LeadDiscoveryCost
{
    /// <summary>
    /// The run's cost in USD: each kind of token at its own per-million rate, plus a per-search charge for web
    /// searches. Six decimal places, because a cheap run genuinely costs fractions of a cent and rounding to
    /// two would report it as zero.
    ///
    /// An estimate from configured list prices - see <see cref="LeadDiscoveryPricingOptions"/> for why that is
    /// not the same as an invoice.
    /// </summary>
    public static decimal Estimate(LeadDiscoveryUsage usage, string? model, LeadDiscoveryPricingOptions pricing)
    {
        var rates = pricing.RatesFor(model);

        var tokenCost = (usage.InputTokens * rates.InputPerMillion
                         + usage.OutputTokens * rates.OutputPerMillion
                         + usage.CacheReadInputTokens * rates.CacheReadPerMillion
                         + usage.CacheCreationInputTokens * rates.CacheWritePerMillion)
                        / 1_000_000m;

        var searchCost = usage.WebSearches * pricing.WebSearchPerThousand / 1_000m;

        return Math.Round(tokenCost + searchCost, 6, MidpointRounding.AwayFromZero);
    }
}
