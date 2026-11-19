using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What a discovery run is priced at: the model that ran it, tokens plus searches, with the two parts adding up to the total.</summary>
public sealed class LeadDiscoveryCostTests
{
    /// <summary>The usage of a real ChatGPT run that found nothing: it was shown as the Claude Haiku price, several times too much.</summary>
    private static readonly LeadDiscoveryUsage RealRun = new(
        InputTokens: 78_603, OutputTokens: 2_895, CacheReadInputTokens: 46_208, CacheCreationInputTokens: 0, WebSearches: 7, WebFetches: 2);

    [Fact]
    public void A_ChatGPT_model_is_priced_at_its_own_rates_not_at_the_default_models()
    {
        // As on a server whose configuration names a Claude model as the default and prices only Claude models.
        var pricing = new LeadDiscoveryPricingOptions { DefaultModel = "claude-haiku-4-5-20251001" };

        Assert.Equal(0.25m, pricing.RatesFor("gpt-5-mini").InputPerMillion);
        Assert.Equal(2m, pricing.RatesFor("gpt-5-mini").OutputPerMillion);
        Assert.Equal(0.025m, pricing.RatesFor("gpt-5-mini").CacheReadPerMillion);
        Assert.Equal(1.25m, pricing.RatesFor("gpt-5").InputPerMillion);
        Assert.Equal(0.05m, pricing.RatesFor("gpt-5-nano").InputPerMillion);
    }

    [Fact]
    public void A_run_on_gpt_5_mini_costs_its_tokens_at_gpt_5_mini_rates_plus_its_searches()
    {
        var pricing = new LeadDiscoveryPricingOptions { DefaultModel = "claude-haiku-4-5-20251001" };

        var cost = LeadDiscoveryCost.Estimate(RealRun, "gpt-5-mini", pricing);

        // tokens: 78,603 x 0.25 + 2,895 x 2 + 46,208 x 0.025 = 26,595.95 / 1M = 0.026596   searches: 7 x $10 / 1000 = 0.07
        Assert.Equal(0.096596m, cost);
    }

    [Fact]
    public void The_same_usage_priced_as_the_default_claude_model_is_the_overstated_figure_that_was_shown()
    {
        var overstated = LeadDiscoveryCost.Estimate(RealRun, "a-model-nobody-priced", new LeadDiscoveryPricingOptions { DefaultModel = "claude-haiku-4-5-20251001" });

        Assert.Equal(0.167699m, overstated);   // the figure on the screen that prompted this
    }

    [Fact]
    public void The_search_part_is_the_searches_times_the_per_search_price()
    {
        Assert.Equal(0.07m, LeadDiscoveryCost.SearchCost(RealRun, new LeadDiscoveryPricingOptions()));
        Assert.Equal(0m, LeadDiscoveryCost.SearchCost(LeadDiscoveryUsage.None, new LeadDiscoveryPricingOptions()));
    }

    [Theory]
    [InlineData("gpt-5-mini")]
    [InlineData("gpt-5")]
    [InlineData("claude-haiku-4-5-20251001")]
    [InlineData("claude-opus-5")]
    public void Every_model_costs_more_than_its_searches_alone_because_the_tokens_are_billed_too(string model)
    {
        var pricing = new LeadDiscoveryPricingOptions();

        Assert.True(LeadDiscoveryCost.Estimate(RealRun, model, pricing) > LeadDiscoveryCost.SearchCost(RealRun, pricing));
    }

    [Fact]
    public void A_dearer_model_costs_more_for_the_same_usage()
    {
        var pricing = new LeadDiscoveryPricingOptions();

        Assert.True(LeadDiscoveryCost.Estimate(RealRun, "gpt-5", pricing) > LeadDiscoveryCost.Estimate(RealRun, "gpt-5-mini", pricing));
        Assert.True(LeadDiscoveryCost.Estimate(RealRun, "gpt-5-mini", pricing) > LeadDiscoveryCost.Estimate(RealRun, "gpt-5-nano", pricing));
    }

    [Fact]
    public void A_run_that_used_nothing_costs_nothing()
    {
        Assert.Equal(0m, LeadDiscoveryCost.Estimate(LeadDiscoveryUsage.None, "gpt-5-mini", new LeadDiscoveryPricingOptions()));
    }
}
