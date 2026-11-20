using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What the plan cost report reads to know which models the platform runs: the chat model of the active provider, and the model chosen for lead discovery.</summary>
internal static class PlanCostTestSupport
{
    private sealed class FixedConfig : IPlatformAiConfigProvider
    {
        private readonly AiCredentials _credentials;
        public FixedConfig(AiCredentials credentials) => _credentials = credentials;
        public AiCredentials Get() => _credentials;
    }

    private sealed class FixedRouting : IAiFeatureRouting
    {
        public AiFeatureChoice Resolve(string feature) => new(true, "Anthropic");
    }

    /// <param name="chatModel">The OpenAI model customer replies run on (null: one with no price anywhere).</param>
    /// <param name="leadModel">The model chosen for lead discovery on Claude (null: none chosen).</param>
    public static (IPlatformAiConfigProvider Config, IAiFeatureRouting Routing) Models(string? chatModel = "gpt-5-mini", string? leadModel = "claude-sonnet-5")
    {
        var taskModels = leadModel is null ? null : new Dictionary<string, string> { [$"{AiModelGroups.LeadDiscovery}/Anthropic"] = leadModel };
        var credentials = new AiCredentials("OpenAI", "Simulated", "a", "claude-haiku-4-5", "v", "u",
            "o", chatModel ?? "a-model-nobody-priced", "e", "u", "g", "m", "e", "u", TaskModels: taskModels);
        return (new FixedConfig(credentials), new FixedRouting());
    }
}
