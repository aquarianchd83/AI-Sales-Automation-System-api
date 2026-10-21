using FluentValidation;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Infrastructure.Integrations;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Which third-party tool each AI feature can run on. The rule lives in one place (AiFeatures.SupportedProviders) and the save,
/// the test call and the feature itself all follow it, so a tool a feature cannot call is never offered, saved or used.</summary>
public class AiFeatureSettingsTests
{
    private sealed class MemoryStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>(Rows, StringComparer.OrdinalIgnoreCase));

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
                Rows[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    /// <summary>Keys for all three tools unless a test says otherwise, so a refusal is about support, not about a missing key.</summary>
    private sealed class FakeCredentials : IPlatformAiConfigProvider
    {
        public string? OpenAiKey { get; init; } = "sk-openai";
        public string? ClaudeKey { get; init; } = "sk-claude";
        public string? GeminiKey { get; init; } = "g-key";
        public string Provider { get; init; } = "Simulated";
        public string Embedding { get; init; } = "Simulated";

        public AiCredentials Get() => new(Provider, Embedding, ClaudeKey, "m", "v", "u", OpenAiKey, "m", "e", "u", GeminiKey, "m", "e", "u");
    }

    private sealed class FixedCredentials : IPlatformAiConfigProvider
    {
        private readonly AiCredentials _value;
        public FixedCredentials(AiCredentials value) => _value = value;
        public AiCredentials Get() => _value;
    }

    private sealed class FakeAi : IAiTextGenerator
    {
        public string? Reply { get; set; } = "OK";
        public string? LastProvider { get; private set; }

        public Task<string?> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default, string? provider = null)
        {
            LastProvider = provider;
            return Task.FromResult(Reply);
        }
    }

    private static PlatformAiFeatureSettingsService Service(MemoryStore store, IPlatformAiConfigProvider? credentials = null, FakeAi? ai = null) =>
        new(store, credentials ?? new FakeCredentials(), ai ?? new FakeAi());

    private static AiFeatureChannelRequest Sim() => new("Simulator", "OpenAI");

    private static AiFeatureChannelRequest Api(string provider) => new("Api", provider);

    /// <summary>Everything on the simulator, with the discovery lead and the conversation on what they support - the base each test bends.</summary>
    private static UpdatePlatformAiFeatureSettingsRequest Valid() => new(Api("Anthropic"), Sim(), Sim(), Sim());

    // ---- what each feature supports ----

    [Fact]
    public void Each_feature_lists_only_the_tools_its_code_can_call()
    {
        Assert.Equal(new[] { "Anthropic" }, AiFeatures.SupportedProviders(AiFeatures.DiscoveryLead));
        Assert.Equal(new[] { "OpenAI", "Anthropic", "Google" }, AiFeatures.SupportedProviders(AiFeatures.Conversation));
        Assert.Empty(AiFeatures.SupportedProviders(AiFeatures.KnowledgeBase));
        Assert.Empty(AiFeatures.SupportedProviders(AiFeatures.ContentGeneration));
        Assert.Empty(AiFeatures.SupportedProviders(AiFeatures.VideoRenders));
        Assert.Equal(new[] { "Anthropic", "OpenAI", "Google" }, AiFeatures.SupportedProviders(AiFeatures.BusinessKeywords));
        Assert.Equal(new[] { "Anthropic", "OpenAI", "Google" }, AiFeatures.SupportedProviders(AiFeatures.DiscoveryKeywords));
        Assert.Empty(AiFeatures.SupportedProviders("Payroll"));
    }

    [Fact]
    public async Task The_page_is_told_what_each_feature_supports_even_when_every_key_is_saved()
    {
        var dto = await Service(new MemoryStore()).GetAsync();

        Assert.Equal(new[] { "Anthropic" }, dto.DiscoveryLead.SupportedProviders);
        Assert.Empty(dto.KnowledgeBase.SupportedProviders);
        Assert.Equal(new[] { "OpenAI", "Anthropic", "Google" }, dto.Conversation.SupportedProviders);
        Assert.Equal(new[] { "OpenAI", "Google" }, dto.Conversation.SupportedEmbeddingProviders);
    }

    [Fact]
    public async Task Nothing_saved_means_discovery_leads_on_Claude_and_everything_else_on_the_simulator()
    {
        var dto = await Service(new MemoryStore()).GetAsync();

        Assert.Equal("Api", dto.DiscoveryLead.Mode);
        Assert.Equal("Anthropic", dto.DiscoveryLead.Provider);
        Assert.True(dto.DiscoveryLead.IsLive);
        Assert.True(dto.DiscoveryLead.SavedProviderSupported);
        Assert.Equal("Simulator", dto.KnowledgeBase.Mode);
        Assert.Equal("Simulator", dto.ContentGeneration.Mode);
        Assert.Equal("Simulator", dto.VideoRenders.Mode);
    }

    [Fact]
    public async Task Business_keywords_were_always_on_a_third_party_tool_so_they_stay_on_Claude_until_chosen()
    {
        var dto = await Service(new MemoryStore()).GetAsync();

        Assert.Equal("Api", dto.BusinessKeywords.Mode);
        Assert.Equal("Anthropic", dto.BusinessKeywords.Provider);
        Assert.True(dto.BusinessKeywords.IsLive);
        Assert.Equal(new[] { "Anthropic", "OpenAI", "Google" }, dto.BusinessKeywords.SupportedProviders);
    }

    [Fact]
    public async Task Discovery_keywords_are_their_own_choice_apart_from_the_discovery_agent()
    {
        var store = new MemoryStore();

        var dto = await Service(store).UpdateAsync(Valid() with { DiscoveryKeywords = Api("OpenAI") }, null);

        Assert.Equal("OpenAI", store.Rows["Integrations:Ai:DiscoveryKeywords:Provider"]);
        Assert.Equal("OpenAI", dto.DiscoveryKeywords.Provider);
        Assert.Equal("Anthropic", dto.DiscoveryLead.Provider); // the agent is untouched
        Assert.Equal(new[] { "Anthropic" }, dto.DiscoveryLead.SupportedProviders);
    }

    [Fact]
    public async Task Saves_the_business_keywords_choice_and_leaves_it_alone_when_the_request_omits_it()
    {
        var store = new MemoryStore();

        await Service(store).UpdateAsync(Valid() with { BusinessKeywords = Api("Google") }, null);
        Assert.Equal("Api", store.Rows["Integrations:Ai:BusinessKeywords:Mode"]);
        Assert.Equal("Google", store.Rows["Integrations:Ai:BusinessKeywords:Provider"]);

        var other = new MemoryStore();
        await Service(other).UpdateAsync(Valid(), null);
        Assert.DoesNotContain(other.Rows.Keys, k => k.Contains("BusinessKeywords"));
    }

    [Fact]
    public async Task Refuses_business_keywords_on_a_tool_whose_key_is_not_saved_and_saves_nothing()
    {
        var store = new MemoryStore();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service(store, new FakeCredentials { GeminiKey = null }).UpdateAsync(Valid() with { BusinessKeywords = Api("Google") }, null));

        Assert.Contains(error.Errors, e => e.PropertyName == "BusinessKeywords.Provider" && e.ErrorMessage.Contains("No Gemini key"));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Refuses_a_tool_that_is_turned_off_under_AI_Providers_even_with_its_key_saved()
    {
        var store = new MemoryStore();
        var off = new AiCredentials("Simulated", "Simulated", "k", "m", "v", "u", "k", "m", "e", "u", null, "m", "e", "u", GoogleEnabled: false);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service(store, new FixedCredentials(off)).UpdateAsync(Valid() with { BusinessKeywords = Api("Google") }, null));

        Assert.Contains(error.Errors, e => e.PropertyName == "BusinessKeywords.Provider" && e.ErrorMessage.Contains("turned off"));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_saved_choice_on_a_tool_that_is_turned_off_is_kept_but_reported_not_live()
    {
        var store = new MemoryStore();
        store.Rows["Integrations:Ai:DiscoveryKeywords:Mode"] = "Api";
        store.Rows["Integrations:Ai:DiscoveryKeywords:Provider"] = "OpenAI";
        var off = new AiCredentials("Simulated", "Simulated", "k", "m", "v", "u", null, "m", "e", "u", "g", "m", "e", "u", OpenAiEnabled: false);

        var dto = await Service(store, new FixedCredentials(off)).GetAsync();

        Assert.Equal("Api", dto.DiscoveryKeywords.Mode);
        Assert.Equal("OpenAI", dto.DiscoveryKeywords.Provider);
        Assert.False(dto.DiscoveryKeywords.ProviderEnabled);
        Assert.False(dto.DiscoveryKeywords.IsLive);
    }

    [Fact]
    public async Task The_test_button_works_for_business_keywords()
    {
        var ai = new FakeAi();

        var result = await Service(new MemoryStore(), ai: ai).TestAsync(AiFeatures.BusinessKeywords, new TestAiFeatureRequest(Api("OpenAI")));

        Assert.True(result.Success);
        Assert.Equal("OpenAI", ai.LastProvider);
    }

    // ---- saving ----

    [Fact]
    public async Task Saves_a_supported_tool_and_keeps_the_rest_on_the_simulator()
    {
        var store = new MemoryStore();

        var dto = await Service(store).UpdateAsync(Valid(), null);

        Assert.Equal("Api", dto.DiscoveryLead.Mode);
        Assert.Equal("Anthropic", store.Rows["Integrations:Ai:DiscoveryLead:Provider"]);
        Assert.Equal("Simulator", store.Rows["Integrations:Ai:KnowledgeBase:Mode"]);
    }

    [Fact]
    public async Task Refuses_a_tool_the_feature_does_not_support_even_with_its_key_saved_and_saves_nothing()
    {
        var store = new MemoryStore();

        // ChatGPT has a key, but discovery leads cannot call it: a request that skipped the page must not get through.
        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service(store).UpdateAsync(Valid() with { DiscoveryLead = Api("OpenAI") }, null));

        Assert.Contains(error.Errors, e => e.PropertyName == "DiscoveryLead.Provider" && e.ErrorMessage.Contains("not available for this feature") && e.ErrorMessage.Contains("Claude"));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Refuses_a_third_party_tool_for_a_feature_that_does_not_use_one_yet()
    {
        var store = new MemoryStore();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service(store).UpdateAsync(Valid() with { ContentGeneration = Api("OpenAI"), VideoRenders = Api("Google") }, null));

        Assert.Contains(error.Errors, e => e.PropertyName == "ContentGeneration.Mode");
        Assert.Contains(error.Errors, e => e.PropertyName == "VideoRenders.Mode");
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Refuses_a_supported_tool_whose_key_is_not_saved()
    {
        var store = new MemoryStore();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service(store, new FakeCredentials { ClaudeKey = null }).UpdateAsync(Valid(), null));

        Assert.Contains(error.Errors, e => e.PropertyName == "DiscoveryLead.Provider" && e.ErrorMessage.Contains("No Claude key"));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Refuses_an_unknown_mode_or_an_unknown_tool()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Service(new MemoryStore()).UpdateAsync(Valid() with { DiscoveryLead = new("Fast", "Anthropic") }, null));
        await Assert.ThrowsAsync<ValidationException>(() => Service(new MemoryStore()).UpdateAsync(Valid() with { DiscoveryLead = Api("Llama") }, null));
    }

    [Fact]
    public async Task The_simulator_needs_no_key_and_keeps_an_earlier_tool_as_it_was()
    {
        var store = new MemoryStore();
        store.Rows["Integrations:Ai:DiscoveryLead:Provider"] = "Anthropic";

        var dto = await Service(store, new FakeCredentials { ClaudeKey = null }).UpdateAsync(Valid() with { DiscoveryLead = Sim() }, null);

        Assert.Equal("Simulator", dto.DiscoveryLead.Mode);
        Assert.False(dto.DiscoveryLead.IsLive);
        Assert.Equal("Anthropic", store.Rows["Integrations:Ai:DiscoveryLead:Provider"]);
    }

    // ---- configurations saved before this rule ----

    [Fact]
    public async Task A_saved_tool_the_feature_no_longer_supports_is_reported_and_left_untouched()
    {
        var store = new MemoryStore();
        store.Rows["Integrations:Ai:DiscoveryLead:Mode"] = "Api";
        store.Rows["Integrations:Ai:DiscoveryLead:Provider"] = "OpenAI";
        store.Rows["Integrations:Ai:KnowledgeBase:Mode"] = "Api";
        store.Rows["Integrations:Ai:KnowledgeBase:Provider"] = "Google";

        var dto = await Service(store).GetAsync();

        Assert.False(dto.DiscoveryLead.SavedProviderSupported);
        Assert.False(dto.DiscoveryLead.IsLive);
        Assert.Equal("OpenAI", dto.DiscoveryLead.Provider);
        Assert.False(dto.KnowledgeBase.SavedProviderSupported);
        Assert.False(dto.KnowledgeBase.IsLive);
        Assert.Equal("OpenAI", store.Rows["Integrations:Ai:DiscoveryLead:Provider"]);
        Assert.Equal("Google", store.Rows["Integrations:Ai:KnowledgeBase:Provider"]);
    }

    [Fact]
    public async Task A_valid_saved_configuration_reads_back_unchanged()
    {
        var store = new MemoryStore();
        store.Rows["Integrations:Ai:DiscoveryLead:Mode"] = "Api";
        store.Rows["Integrations:Ai:DiscoveryLead:Provider"] = "Anthropic";

        var dto = await Service(store).GetAsync();

        Assert.Equal("Api", dto.DiscoveryLead.Mode);
        Assert.Equal("Anthropic", dto.DiscoveryLead.Provider);
        Assert.True(dto.DiscoveryLead.ProviderHasKey);
        Assert.True(dto.DiscoveryLead.IsLive);
        Assert.True(dto.DiscoveryLead.SavedProviderSupported);
    }

    // ---- the conversation ----

    [Fact]
    public async Task Conversation_shows_what_the_engine_really_runs_on()
    {
        var dto = await Service(new MemoryStore(), new FakeCredentials { Provider = "OpenAI", Embedding = "Google" }).GetAsync();

        Assert.Equal("Api", dto.Conversation.Mode);
        Assert.Equal("OpenAI", dto.Conversation.Provider);
        Assert.Equal("Google", dto.Conversation.EmbeddingProvider);
        Assert.True(dto.Conversation.IsLive);

        var simulated = await Service(new MemoryStore()).GetAsync();
        Assert.Equal("Simulator", simulated.Conversation.Mode);
        Assert.Equal("Simulator", simulated.Conversation.EmbeddingMode);
    }

    [Fact]
    public async Task On_the_simulator_the_tool_shown_for_picking_reports_whether_its_key_is_saved()
    {
        // Both are on the Simulator, so the page offers the first tool (ChatGPT) to pick; its key IS saved, so picking it must not say "No ChatGPT key is saved yet".
        var dto = await Service(new MemoryStore(), new FakeCredentials()).GetAsync();

        Assert.Equal(("Simulator", "OpenAI", true), (dto.Conversation.Mode, dto.Conversation.Provider, dto.Conversation.ProviderHasKey));
        Assert.Equal(("Simulator", "OpenAI", true), (dto.Conversation.EmbeddingMode, dto.Conversation.EmbeddingProvider, dto.Conversation.EmbeddingProviderHasKey));

        var noKey = await Service(new MemoryStore(), new FakeCredentials { OpenAiKey = null }).GetAsync();
        Assert.False(noKey.Conversation.ProviderHasKey);
        Assert.False(noKey.Conversation.EmbeddingProviderHasKey);
    }

    [Fact]
    public async Task Conversation_is_saved_where_the_engine_reads_it()
    {
        var store = new MemoryStore();
        var service = Service(store);

        await service.UpdateAsync(Valid() with { Conversation = new AiConversationRequest("Api", "Anthropic", "Simulator", "OpenAI") }, null);
        Assert.Equal("Anthropic", store.Rows["AiProviders:Provider"]);
        Assert.Equal("Simulated", store.Rows["AiProviders:EmbeddingProvider"]);

        await service.UpdateAsync(Valid() with { Conversation = new AiConversationRequest("Simulator", "Anthropic", "Api", "Google") }, null);
        Assert.Equal("Simulated", store.Rows["AiProviders:Provider"]);
        Assert.Equal("Google", store.Rows["AiProviders:EmbeddingProvider"]);
    }

    [Fact]
    public async Task Conversation_left_out_is_left_alone_and_claude_cannot_embed()
    {
        var store = new MemoryStore();
        var service = Service(store);

        await service.UpdateAsync(Valid(), null);
        Assert.DoesNotContain("AiProviders:Provider", store.Rows.Keys);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(Valid() with { Conversation = new AiConversationRequest("Api", "OpenAI", "Api", "Anthropic") }, null));
        Assert.Contains(error.Errors, e => e.PropertyName == "Conversation.EmbeddingProvider");
    }

    // ---- the test call ----

    [Fact]
    public async Task Test_asks_the_chosen_supported_tool_and_reports_the_answer()
    {
        var ai = new FakeAi();

        var ok = await Service(new MemoryStore(), ai: ai).TestAsync(AiFeatures.DiscoveryLead, new TestAiFeatureRequest(Api("Anthropic")));
        Assert.Equal("Anthropic", ai.LastProvider);
        Assert.True(ok.Success);
        Assert.Contains("Claude", ok.Message);

        ai.Reply = null;
        var failed = await Service(new MemoryStore(), ai: ai).TestAsync(AiFeatures.DiscoveryLead, new TestAiFeatureRequest(Api("Anthropic")));
        Assert.False(failed.Success);
    }

    [Fact]
    public async Task Test_refuses_a_tool_the_feature_does_not_support_without_calling_it()
    {
        var ai = new FakeAi();

        await Assert.ThrowsAsync<ValidationException>(() =>
            Service(new MemoryStore(), ai: ai).TestAsync(AiFeatures.DiscoveryLead, new TestAiFeatureRequest(Api("OpenAI"))));

        Assert.Null(ai.LastProvider);
    }

    [Fact]
    public async Task Test_on_the_simulator_calls_nothing()
    {
        var ai = new FakeAi();

        var result = await Service(new MemoryStore(), ai: ai).TestAsync(AiFeatures.VideoRenders, new TestAiFeatureRequest(Sim()));

        Assert.True(result.Success);
        Assert.Null(ai.LastProvider);
    }

    [Fact]
    public async Task Test_rejects_an_unknown_feature()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service(new MemoryStore()).TestAsync("Payroll", new TestAiFeatureRequest(Api("OpenAI"))));
    }

    [Fact]
    public async Task Conversation_can_be_tested_on_any_of_its_tools()
    {
        var ai = new FakeAi();

        var result = await Service(new MemoryStore(), ai: ai).TestAsync(AiFeatures.Conversation, new TestAiFeatureRequest(Api("Google")));

        Assert.True(result.Success);
        Assert.Equal("Google", ai.LastProvider);
    }

    // ---- the feature itself follows the same rule ----

    private sealed class Snapshot : IOptionsSnapshot<AiFeatureOptions>
    {
        public Snapshot(AiFeatureOptions value) => Value = value;
        public AiFeatureOptions Value { get; }
        public AiFeatureOptions Get(string? name) => Value;
    }

    [Fact]
    public void Routing_uses_the_default_until_a_choice_is_saved_then_the_choice()
    {
        var unset = new AiFeatureRouting(new Snapshot(new AiFeatureOptions()));
        Assert.Equal(new AiFeatureChoice(true, "Anthropic"), unset.Resolve(AiFeatures.DiscoveryLead));
        Assert.False(unset.Resolve(AiFeatures.VideoRenders).UsesThirdParty);
        Assert.Equal(new AiFeatureChoice(true, "Anthropic"), unset.Resolve(AiFeatures.BusinessKeywords));

        var simulated = new AiFeatureRouting(new Snapshot(new AiFeatureOptions
        {
            DiscoveryLead = new AiFeatureOption { Mode = "Simulator", Provider = "Anthropic" },
        }));
        Assert.False(simulated.Resolve(AiFeatures.DiscoveryLead).UsesThirdParty);

        var keywords = new AiFeatureRouting(new Snapshot(new AiFeatureOptions
        {
            BusinessKeywords = new AiFeatureOption { Mode = "Api", Provider = "google" },
        }));
        Assert.Equal(new AiFeatureChoice(true, "Google"), keywords.Resolve(AiFeatures.BusinessKeywords));
    }

    [Fact]
    public void Routing_never_calls_a_tool_the_feature_does_not_support()
    {
        var routing = new AiFeatureRouting(new Snapshot(new AiFeatureOptions
        {
            DiscoveryLead = new AiFeatureOption { Mode = "Api", Provider = "OpenAI" },
            VideoRenders = new AiFeatureOption { Mode = "Api", Provider = "anthropic" },
        }));

        Assert.False(routing.Resolve(AiFeatures.DiscoveryLead).UsesThirdParty);
        Assert.False(routing.Resolve(AiFeatures.VideoRenders).UsesThirdParty);
    }
}
