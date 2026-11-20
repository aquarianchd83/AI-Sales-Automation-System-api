using WhatsAppSalesAutomation.Application.LeadDiscovery;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The history grid's AI provider column: a run records the model it used, and the provider is read from it.</summary>
public class LeadDiscoveryAiProviderTests
{
    [Theory]
    [InlineData("gpt-5-mini", "OpenAI")]
    [InlineData("GPT-5-nano", "OpenAI")]
    [InlineData("o4-mini", "OpenAI")]
    [InlineData("claude-sonnet-5-5", "Anthropic")]
    [InlineData("Simulated", "Simulated")]
    [InlineData("gemini-2.5-pro", "Google")]
    [InlineData("some-new-model", "some-new-model")]
    public void The_provider_is_read_from_the_model_the_run_recorded(string model, string provider) =>
        Assert.Equal(provider, LeadDiscoveryHistoryService.ProviderOf(model));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void An_execution_that_did_no_research_has_no_provider(string? model) =>
        Assert.Null(LeadDiscoveryHistoryService.ProviderOf(model));
}
