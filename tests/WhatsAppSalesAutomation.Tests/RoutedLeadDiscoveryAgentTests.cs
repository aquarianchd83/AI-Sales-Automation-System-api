using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Discovery lead choice on the Integrations page (simulator, ChatGPT, Claude) decides which engine a run uses. The paths tested
/// here never reach the AI call recorder (no real call is made), so none is supplied.</summary>
public sealed class RoutedLeadDiscoveryAgentTests
{
    private static readonly LeadDiscoveryAgentRequest Request = new(
        "dental clinic", new[] { "dentist" }, new[] { "Pune" }, 3,
        Array.Empty<string>(), false, false, false, 0, Array.Empty<string>(), Array.Empty<string>());

    private sealed class FixedRouting : IAiFeatureRouting
    {
        private readonly AiFeatureChoice _choice;
        public FixedRouting(AiFeatureChoice choice) => _choice = choice;
        public AiFeatureChoice Resolve(string feature) => _choice;
    }

    /// <summary>The platform has no keys saved at all.</summary>
    private sealed class NoKeys : IPlatformAiConfigProvider
    {
        public AiCredentials Get() => new("Simulated", "Simulated", null, "m", "v", "https://api.anthropic.com/v1/",
            null, "m", "e", "https://api.openai.com/v1/", null, "m", "e", "u");
    }

    /// <summary>The agents read their settings per run, through a snapshot.</summary>
    private sealed class FixedSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public FixedSnapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }

    private static RoutedLeadDiscoveryAgent Agent(AiFeatureChoice choice)
    {
        var settings = new FixedSnapshot<LeadDiscoveryAgentSettings>(new LeadDiscoveryAgentSettings());
        var keys = new NoKeys();
        return new RoutedLeadDiscoveryAgent(
            new FixedRouting(choice),
            new SimulatedLeadDiscoveryAgent(NullLogger<SimulatedLeadDiscoveryAgent>.Instance),
            new OpenAiLeadDiscoveryAgent(new HttpClient(), settings, NullLogger<OpenAiLeadDiscoveryAgent>.Instance, keys,
                new LeadDiscoveryPageFetcher(new HttpClient(LeadDiscoveryPageFetcher.CreateHandler()))),
            new AnthropicLeadDiscoveryAgent(new HttpClient(), settings, NullLogger<AnthropicLeadDiscoveryAgent>.Instance, keys),
            recorder: null!);
    }

    [Fact]
    public async Task The_simulator_choice_runs_the_simulated_agent()
    {
        var result = await Agent(new AiFeatureChoice(false, "Anthropic")).DiscoverAsync(Request);

        Assert.True(result.IsConfigured);
        Assert.Equal(SimulatedLeadDiscoveryAgent.ModelName, result.Model);
    }

    [Fact]
    public async Task The_ChatGPT_choice_runs_the_OpenAI_agent()
    {
        var result = await Agent(new AiFeatureChoice(true, "OpenAI")).DiscoverAsync(Request);

        Assert.False(result.IsConfigured);
        Assert.Contains("OpenAI", result.NotConfiguredReason);
    }

    [Fact]
    public async Task The_Claude_choice_runs_the_Anthropic_agent()
    {
        var result = await Agent(new AiFeatureChoice(true, "Anthropic")).DiscoverAsync(Request);

        Assert.False(result.IsConfigured);
        Assert.Contains("Anthropic", result.NotConfiguredReason);
    }

    [Fact]
    public async Task A_tool_discovery_cannot_run_on_falls_back_to_the_simulator()
    {
        var result = await Agent(new AiFeatureChoice(true, "Google")).DiscoverAsync(Request);

        Assert.Equal(SimulatedLeadDiscoveryAgent.ModelName, result.Model);
    }
}
