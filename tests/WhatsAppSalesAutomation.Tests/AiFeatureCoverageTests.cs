using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Infrastructure.Integrations;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// An AI feature needs three things to keep its Integrations choice: a catalog entry (so the store accepts the value), a property on the bound options
/// (so the feature reads it at request time) and a place in the settings service (so the page gets it back). Adding a feature to one and forgetting
/// another makes the page appear to forget the choice after Save - which is exactly what happened to the story script - so each is checked for every feature.
/// </summary>
public class AiFeatureCoverageTests
{
    private sealed class MemoryStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>(Rows, StringComparer.OrdinalIgnoreCase));

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            // Like the real store: a key the catalog does not list is silently not saved.
            var known = AppSettingCatalog.All.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values)
                if (known.Contains(key)) Rows[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class Credentials : IPlatformAiConfigProvider
    {
        public AiCredentials Get() => new("Simulated", "Simulated", "k", "m", "v", "u", "k", "m", "e", "u", "k", "m", "e", "u");
    }

    private sealed class NoAi : IAiTextGenerator
    {
        public Task<string?> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default, string? provider = null) => Task.FromResult<string?>("OK");
    }

    private sealed class Snapshot : IOptionsSnapshot<AiFeatureOptions>
    {
        public AiFeatureOptions Value { get; init; } = new();
        public AiFeatureOptions Get(string? name) => Value;
    }

    /// <summary>Every feature that has a Mode and a Provider on the Integrations page (the conversation engine is stored elsewhere).</summary>
    public static IEnumerable<object[]> Features() => AiFeatures.All.Where(f => f != AiFeatures.Conversation).Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Features))]
    public void The_catalog_accepts_the_features_mode_and_provider(string feature)
    {
        var keys = AppSettingCatalog.All.Select(d => d.Key).ToHashSet();

        Assert.Contains(AiFeatures.Prefix(feature) + ":Mode", keys);
        Assert.Contains(AiFeatures.Prefix(feature) + ":Provider", keys);
    }

    [Theory]
    [MemberData(nameof(Features))]
    public void The_feature_reads_what_the_page_saved_at_request_time(string feature)
    {
        var supported = AiFeatures.SupportedProviders(feature);
        if (supported.Count == 0) return;   // nothing to route to yet

        var provider = supported.Last();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AiFeatures.Prefix(feature) + ":Mode"] = "Api",
            [AiFeatures.Prefix(feature) + ":Provider"] = provider
        }).Build();
        var options = config.GetSection("Integrations:Ai").Get<AiFeatureOptions>()!;

        var choice = new AiFeatureRouting(new Snapshot { Value = options }).Resolve(feature);

        Assert.True(choice.UsesThirdParty, $"{feature}: the saved choice never reached the feature, so it would stay on the simulator.");
        Assert.Equal(provider, choice.Provider);
    }

    [Theory]
    [MemberData(nameof(Features))]
    public async Task A_saved_choice_comes_back_on_the_next_load_instead_of_resetting(string feature)
    {
        var supported = AiFeatures.SupportedProviders(feature);
        if (supported.Count == 0) return;

        var store = new MemoryStore();
        var service = new PlatformAiFeatureSettingsService(store, new Credentials(), new NoAi());
        var sim = new AiFeatureChannelRequest("Simulator", "OpenAI");
        var chosen = new AiFeatureChannelRequest("Api", supported.Last());
        var request = new UpdatePlatformAiFeatureSettingsRequest(
            feature == AiFeatures.DiscoveryLead ? chosen : new AiFeatureChannelRequest("Api", "Anthropic"), sim, sim, sim,
            BusinessKeywords: feature == AiFeatures.BusinessKeywords ? chosen : null,
            DiscoveryKeywords: feature == AiFeatures.DiscoveryKeywords ? chosen : null,
            ProductKnowledgeTasks: AiFeatures.IsProductTaskGroup(feature) ? new[] { new AiTaskGroupRequest(feature, chosen.Mode, chosen.Provider) } : null);

        var saved = await service.UpdateAsync(request, null);

        var channel = feature switch
        {
            AiFeatures.DiscoveryLead => saved.DiscoveryLead,
            AiFeatures.BusinessKeywords => saved.BusinessKeywords,
            AiFeatures.DiscoveryKeywords => saved.DiscoveryKeywords,
            _ when AiFeatures.IsProductTaskGroup(feature) => saved.ProductKnowledgeTasks.Single(g => g.Feature == feature).Channel,
            _ => null
        };
        if (channel is null) return;

        Assert.Equal("Api", channel.Mode);
        Assert.Equal(supported.Last(), channel.Provider);
        Assert.True(store.Rows.ContainsKey(AiFeatures.Prefix(feature) + ":Mode"));
    }

    [Fact]
    public void The_story_script_is_one_of_the_features_checked_above()
    {
        Assert.Contains(AiFeatures.StoryScript, Features().Select(f => (string)f[0]));
    }
}
