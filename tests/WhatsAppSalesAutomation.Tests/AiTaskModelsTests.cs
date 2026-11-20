using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>"Model by task": each group of AI tasks can run on its own model per provider, and runs on the provider's chat model when none is chosen.</summary>
public sealed class AiTaskModelsTests
{
    private static AiCredentials Credentials(Dictionary<string, string>? taskModels = null, string? googleKey = "g") =>
        new("Anthropic", "Simulated", "a", "claude-haiku-4-5-20251001", "v", "https://api.anthropic.com/v1/",
            "o", "gpt-5-nano", "e", "https://api.openai.com/v1/", googleKey, "gemini-flash-lite-latest", "e", "https://generativelanguage.googleapis.com/v1beta/",
            TaskModels: taskModels);

    private sealed class FixedConfig : IPlatformAiConfigProvider
    {
        private readonly AiCredentials _credentials;
        public FixedConfig(AiCredentials credentials) => _credentials = credentials;
        public AiCredentials Get() => _credentials;
    }

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

    private sealed class FixedSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public FixedSnapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }

    /// <summary>The models each provider lists (none for a provider not named), as the provider's own API would return them.</summary>
    private sealed class FakeCatalog : IProviderModelCatalog
    {
        private readonly Dictionary<string, string[]> _listed;
        public FakeCatalog(Dictionary<string, string[]>? listed = null) => _listed = listed ?? new();
        public Task<IReadOnlyList<string>> ListAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_listed.GetValueOrDefault(provider) ?? Array.Empty<string>());
        public Task<IReadOnlyList<string>> ListAsync(string provider, WhatsAppSalesAutomation.Domain.Entities.Platform.AiModelType type, CancellationToken cancellationToken = default) =>
            ListAsync(provider, cancellationToken);
    }

    private static PlatformAiTaskModelsService Service(
        MemoryStore store, AiCredentials? credentials = null, AiPricingOptions? pricing = null, Dictionary<string, string[]>? listed = null) =>
        new(store, new FixedConfig(credentials ?? Credentials()), new FixedSnapshot<AiPricingOptions>(pricing ?? new AiPricingOptions()),
            new FixedSnapshot<LeadDiscoveryPricingOptions>(new LeadDiscoveryPricingOptions()), new FakeCatalog(listed));

    private static MemoryStore LeadModelsSaved()
    {
        var store = new MemoryStore();
        store.Rows["AiProviders:TaskModels:LeadDiscovery:Anthropic"] = "claude-haiku-4-5-20251001";
        store.Rows["AiProviders:TaskModels:LeadDiscovery:OpenAI"] = "gpt-5-mini";
        store.Rows["AiProviders:TaskModels:LeadDiscovery:Google"] = "gemini-2.5-flash";
        return store;
    }

    // ---- which group a task belongs to ----

    [Theory]
    [InlineData(AiTaskKinds.ConversationReply, AiModelGroups.CustomerReplies)]
    [InlineData(AiTaskKinds.DocumentAnalysis, AiModelGroups.ProductKnowledge)]
    [InlineData(AiTaskKinds.ProductInfoExtraction, AiModelGroups.ProductKnowledge)]
    [InlineData(AiTaskKinds.ScannedDocumentReading, AiModelGroups.ProductKnowledge)]
    [InlineData(AiTaskKinds.BusinessKeywordSuggestion, AiModelGroups.KeywordsAndRewrites)]
    [InlineData(AiTaskKinds.DescriptionRefinement, AiModelGroups.KeywordsAndRewrites)]
    [InlineData(AiTaskKinds.DiscoveryKeywordSuggestion, AiModelGroups.KeywordsAndRewrites)]
    public void Each_task_belongs_to_its_group(string task, string group) => Assert.Equal(group, AiModelGroups.ForTask(task));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(AiTaskKinds.LeadDiscovery)]
    [InlineData(AiTaskKinds.ConnectionCheck)]
    [InlineData(AiTaskKinds.Benchmark)]
    public void A_task_in_no_group_has_none(string? task) => Assert.Null(AiModelGroups.ForTask(task));

    [Fact]
    public void Every_product_knowledge_task_is_in_a_group()
    {
        Assert.All(AiTaskKinds.Product, task => Assert.Equal(AiModelGroups.ProductKnowledge, AiModelGroups.ForTask(task)));
    }

    // ---- what the credentials say for a group ----

    [Fact]
    public void A_group_runs_on_its_chosen_model_and_the_other_providers_keep_their_chat_models()
    {
        var credentials = Credentials(new() { [$"{AiModelGroups.ProductKnowledge}/Anthropic"] = "claude-sonnet-5" });

        var group = credentials.ForGroup(AiModelGroups.ProductKnowledge);

        Assert.Equal("claude-sonnet-5", group.AnthropicModel);
        Assert.Equal("gpt-5-nano", group.OpenAiChatModel);
        Assert.Equal("gemini-flash-lite-latest", group.GoogleChatModel);
    }

    [Fact]
    public void A_choice_for_one_group_does_not_touch_another()
    {
        var credentials = Credentials(new() { [$"{AiModelGroups.ProductKnowledge}/Anthropic"] = "claude-sonnet-5" });

        Assert.Equal("claude-haiku-4-5-20251001", credentials.ForGroup(AiModelGroups.CustomerReplies).AnthropicModel);
        Assert.Equal("claude-haiku-4-5-20251001", credentials.ForGroup(AiModelGroups.KeywordsAndRewrites).AnthropicModel);
    }

    [Fact]
    public void A_task_in_no_group_and_a_blank_choice_both_run_on_the_chat_model()
    {
        var credentials = Credentials(new() { [$"{AiModelGroups.CustomerReplies}/OpenAI"] = "  " });

        Assert.Same(credentials, credentials.ForGroup(null));
        Assert.Equal("gpt-5-nano", credentials.ForGroup(AiModelGroups.CustomerReplies).OpenAiChatModel);
    }

    [Fact]
    public void The_platform_config_reads_the_group_models_from_the_AiProviders_settings()
    {
        var settings = new AiProviderSettings();
        settings.TaskModels["ProductKnowledge"] = new Dictionary<string, string?> { ["Anthropic"] = "claude-sonnet-5", ["OpenAI"] = "" };
        var provider = new PlatformAiConfigProvider(new FixedSnapshot<AiProviderSettings>(settings));

        var credentials = provider.Get();

        Assert.Equal("claude-sonnet-5", credentials.ForGroup(AiModelGroups.ProductKnowledge).AnthropicModel);
        Assert.Single(credentials.TaskModels!); // the blank one is not a choice
    }

    // ---- the page ----

    [Fact]
    public async Task Nothing_chosen_means_every_group_runs_on_the_chat_model_of_each_provider()
    {
        var dto = await Service(new MemoryStore()).GetAsync();

        Assert.Equal(new[] { "CustomerReplies", "ProductKnowledge", "KeywordsAndRewrites", "LeadDiscovery" }, dto.Groups.Select(g => g.Key));
        Assert.All(dto.Groups.SelectMany(g => g.Providers), cell =>
        {
            Assert.Null(cell.Selected);
            Assert.Equal(cell.ChatModel, cell.EffectiveModel);
        });
        Assert.All(dto.Groups.Where(g => g.Key != "LeadDiscovery"), g => Assert.Equal("Same as chat model", g.DefaultLabel));
        var claude = dto.Groups[0].Providers.Single(p => p.Provider == "Anthropic");
        Assert.Equal("Claude", claude.ProviderLabel);
        Assert.Equal("claude-haiku-4-5-20251001", claude.EffectiveModel);
    }

    [Fact]
    public async Task A_saved_choice_is_what_the_group_runs_on_and_is_priced_from_the_charges_page()
    {
        var store = new MemoryStore();
        store.Rows[AiModelGroups.SettingKey("ProductKnowledge", "Anthropic")] = "claude-sonnet-4-5";

        var cell = (await Service(store).GetAsync()).Groups.Single(g => g.Key == "ProductKnowledge").Providers.Single(p => p.Provider == "Anthropic");

        Assert.Equal("claude-sonnet-4-5", cell.Selected);
        Assert.Equal("claude-sonnet-4-5", cell.EffectiveModel);
        Assert.True(cell.HasPrice);
        Assert.Equal(3m, cell.InputPer1M);
        Assert.Equal(15m, cell.OutputPer1M);
    }

    [Fact]
    public async Task A_model_with_no_price_is_flagged_so_it_is_not_silently_counted_as_free()
    {
        var gemini = (await Service(new MemoryStore()).GetAsync()).Groups[0].Providers.Single(p => p.Provider == "Google");

        // The configured Gemini alias has a price row; a model the catalogue lists with no row stays flagged.
        Assert.True(gemini.HasPrice);
        Assert.Equal(0.1m, gemini.InputPer1M);
        Assert.Contains(gemini.Options, o => o.Model == "gemini-flash-lite-latest" && o.HasPrice);
        Assert.Null(new WhatsAppSalesAutomation.Application.Common.Options.AiPricingOptions().RatesForConfiguredModel("Google", "gemini-unlisted-model"));
    }

    [Fact]
    public async Task A_dated_chat_model_is_priced_from_its_undated_row()
    {
        var claude = (await Service(new MemoryStore()).GetAsync()).Groups[0].Providers.Single(p => p.Provider == "Anthropic");

        Assert.True(claude.HasPrice); // claude-haiku-4-5-20251001 matches the claude-haiku-4-5 row
        Assert.Equal(1m, claude.InputPer1M);
        Assert.Equal(5m, claude.OutputPer1M);
    }

    [Fact]
    public async Task The_choices_offered_are_that_providers_priced_models_plus_the_chat_model()
    {
        var claude = (await Service(new MemoryStore()).GetAsync()).Groups[0].Providers.Single(p => p.Provider == "Anthropic");
        var models = claude.Options.Select(o => o.Model).ToList();

        Assert.Contains("claude-haiku-4-5", models);
        Assert.Contains("claude-sonnet-4-5", models);
        Assert.Contains("claude-haiku-4-5-20251001", models); // the configured chat model, even though it has no row of its own
        Assert.DoesNotContain(models, m => m.StartsWith("gpt"));
    }

    [Fact]
    public async Task A_provider_without_a_key_is_shown_as_unavailable()
    {
        var dto = await Service(new MemoryStore(), Credentials(googleKey: null)).GetAsync();

        Assert.False(dto.Groups[0].Providers.Single(p => p.Provider == "Google").Available);
        Assert.True(dto.Groups[0].Providers.Single(p => p.Provider == "Anthropic").Available);
    }

    // ---- lead discovery: the fourth group ----

    [Fact]
    public async Task Lead_discovery_has_no_default_model_in_code_so_nothing_saved_means_nothing_chosen()
    {
        var group = (await Service(new MemoryStore()).GetAsync()).Groups.Single(g => g.Key == "LeadDiscovery");

        Assert.All(group.Providers, cell =>
        {
            Assert.Null(cell.Selected);
            Assert.Equal(string.Empty, cell.EffectiveModel);
        });
    }

    [Fact]
    public async Task Lead_discovery_shows_the_models_saved_for_it()
    {
        var group = (await Service(LeadModelsSaved()).GetAsync()).Groups.Single(g => g.Key == "LeadDiscovery");

        Assert.Equal("Default", group.DefaultLabel);
        var claude = group.Providers.Single(p => p.Provider == "Anthropic");
        Assert.Equal("claude-haiku-4-5-20251001", claude.EffectiveModel);
        Assert.Equal("gpt-5-mini", group.Providers.Single(p => p.Provider == "OpenAI").EffectiveModel);
        Assert.Equal("gemini-2.5-flash", group.Providers.Single(p => p.Provider == "Google").EffectiveModel);
    }

    [Fact]
    public async Task Lead_discovery_is_priced_from_its_own_table_with_dated_ids_read_from_their_undated_row()
    {
        var group = (await Service(LeadModelsSaved()).GetAsync()).Groups.Single(g => g.Key == "LeadDiscovery");

        var claude = group.Providers.Single(p => p.Provider == "Anthropic");
        Assert.True(claude.HasPrice);
        Assert.Equal(1m, claude.InputPer1M);
        Assert.Equal(5m, claude.OutputPer1M);

        var openAi = group.Providers.Single(p => p.Provider == "OpenAI");
        Assert.Equal(0.25m, openAi.InputPer1M);
        Assert.Equal(2m, openAi.OutputPer1M);

        var gemini = group.Providers.Single(p => p.Provider == "Google");
        Assert.Equal(0.3m, gemini.InputPer1M);
        Assert.Equal(2.5m, gemini.OutputPer1M);
    }

    [Fact]
    public async Task Lead_discovery_offers_each_provider_only_its_own_priced_models()
    {
        var group = (await Service(LeadModelsSaved()).GetAsync()).Groups.Single(g => g.Key == "LeadDiscovery");

        Assert.Equal(new[] { "gpt-5-nano", "gpt-5-mini", "gpt-5" }, group.Providers.Single(p => p.Provider == "OpenAI").Options.Select(o => o.Model));
        Assert.DoesNotContain(group.Providers.Single(p => p.Provider == "Anthropic").Options, o => o.Model == "Simulated");
        Assert.Contains(group.Providers.Single(p => p.Provider == "Google").Options, o => o.Model == "gemini-2.5-pro");
    }

    [Fact]
    public async Task A_lead_discovery_choice_is_saved_and_shown_like_any_other()
    {
        var store = new MemoryStore();

        var dto = await Service(store).UpdateAsync(new UpdateAiTaskModelsRequest(new[] { new AiTaskModelChoiceRequest("LeadDiscovery", "OpenAI", "gpt-5") }), null);

        Assert.Equal("gpt-5", store.Rows["AiProviders:TaskModels:LeadDiscovery:OpenAI"]);
        var cell = dto.Groups.Single(g => g.Key == "LeadDiscovery").Providers.Single(p => p.Provider == "OpenAI");
        Assert.Equal("gpt-5", cell.Selected);
        Assert.Equal(1.25m, cell.InputPer1M);
    }

    [Fact]
    public async Task Lead_discovery_offers_no_blank_while_the_other_groups_do()
    {
        var dto = await Service(new MemoryStore()).GetAsync();

        Assert.False(dto.Groups.Single(g => g.Key == "LeadDiscovery").AllowDefault);
        Assert.All(dto.Groups.Where(g => g.Key != "LeadDiscovery"), g => Assert.True(g.AllowDefault));
    }

    [Fact]
    public async Task A_blank_lead_discovery_model_is_refused_and_nothing_is_saved()
    {
        var store = new MemoryStore();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service(store).UpdateAsync(new UpdateAiTaskModelsRequest(new[] { new AiTaskModelChoiceRequest("LeadDiscovery", "OpenAI", "") }), null));

        Assert.Contains(error.Errors, e => e.ErrorMessage.Contains("Choose a model for Lead discovery"));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public void The_lead_discovery_models_are_seeded_into_the_database_from_the_current_config_then_the_default()
    {
        var rows = new[] { "Anthropic", "OpenAI", "Google" }
            .Select(p => WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All.Single(d => d.Key == $"AiProviders:TaskModels:LeadDiscovery:{p}"))
            .ToList();

        Assert.Equal(new[] { "claude-haiku-4-5-20251001", "gpt-5-mini", "gemini-2.5-flash" }, rows.Select(r => r.DefaultValue));
        Assert.Equal(new[] { "LeadDiscovery:Agent:Model", "LeadDiscovery:Agent:OpenAiModel", "LeadDiscovery:Agent:GeminiModel" }, rows.Select(r => r.SeedFromKey));
    }

    [Fact]
    public void The_credentials_say_which_model_was_chosen_for_lead_discovery_and_null_when_none_was()
    {
        var credentials = Credentials(new() { ["LeadDiscovery/OpenAI"] = "gpt-5" });

        Assert.Equal("gpt-5", credentials.ModelChosenFor(AiModelGroups.LeadDiscovery, "OpenAI"));
        Assert.Null(credentials.ModelChosenFor(AiModelGroups.LeadDiscovery, "Anthropic"));
        Assert.Null(Credentials().ModelChosenFor(AiModelGroups.LeadDiscovery, "OpenAI"));
    }

    // ---- every model the provider offers, cheapest first ----

    [Fact]
    public async Task Every_model_the_provider_lists_is_offered_cheapest_first_and_those_with_no_price_last()
    {
        var dto = await Service(new MemoryStore(), listed: new() { ["OpenAI"] = new[] { "gpt-5", "gpt-4.1-nano", "gpt-5-nano", "o3-mini" } }).GetAsync();

        var options = dto.Groups[0].Providers.Single(p => p.Provider == "OpenAI").Options;

        // priced: gpt-5-nano 0.05+0.4, gpt-5-mini 0.25+2, gpt-5 1.25+10; then the unpriced ones by name
        Assert.Equal(new[] { "gpt-5-nano", "gpt-5-mini", "gpt-5", "gpt-4.1-nano", "o3-mini" }, options.Select(o => o.Model));
        Assert.All(options.Take(3), o => Assert.True(o.HasPrice));
        Assert.All(options.Skip(3), o => Assert.False(o.HasPrice));
    }

    [Fact]
    public async Task A_model_is_offered_once_even_when_it_is_listed_and_priced_and_in_use()
    {
        var dto = await Service(new MemoryStore(), listed: new() { ["OpenAI"] = new[] { "GPT-5-NANO", "gpt-5-nano" } }).GetAsync();

        var options = dto.Groups[0].Providers.Single(p => p.Provider == "OpenAI").Options;

        Assert.Single(options, o => string.Equals(o.Model, "gpt-5-nano", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_provider_that_lists_nothing_still_offers_the_priced_models_and_the_one_in_use()
    {
        var options = (await Service(new MemoryStore()).GetAsync()).Groups[0].Providers.Single(p => p.Provider == "Anthropic").Options;

        Assert.Contains(options, o => o.Model == "claude-sonnet-4-5");
        Assert.Contains(options, o => o.Model == "claude-haiku-4-5-20251001");
    }

    [Fact]
    public async Task The_models_a_provider_lists_are_offered_to_lead_discovery_too_priced_from_its_own_table()
    {
        var group = (await Service(LeadModelsSaved(), listed: new() { ["Anthropic"] = new[] { "claude-opus-5", "claude-3-7-sonnet-latest" } }).GetAsync())
            .Groups.Single(g => g.Key == "LeadDiscovery");

        var options = group.Providers.Single(p => p.Provider == "Anthropic").Options;
        Assert.Equal("claude-opus-5", options.First(o => o.Model == "claude-opus-5").Model);
        Assert.True(options.Single(o => o.Model == "claude-opus-5").HasPrice);
        Assert.False(options.Single(o => o.Model == "claude-3-7-sonnet-latest").HasPrice);
        Assert.Equal("claude-3-7-sonnet-latest", options.Last().Model);
    }

    // ---- saving ----

    [Fact]
    public async Task Saves_each_choice_under_its_own_key_and_returns_the_page()
    {
        var store = new MemoryStore();

        var dto = await Service(store).UpdateAsync(new UpdateAiTaskModelsRequest(new[]
        {
            new AiTaskModelChoiceRequest("customerreplies", "anthropic", "claude-haiku-4-5"),
            new AiTaskModelChoiceRequest("ProductKnowledge", "OpenAI", "gpt-5"),
        }), null);

        Assert.Equal("claude-haiku-4-5", store.Rows["AiProviders:TaskModels:CustomerReplies:Anthropic"]);
        Assert.Equal("gpt-5", store.Rows["AiProviders:TaskModels:ProductKnowledge:OpenAI"]);
        Assert.Equal("gpt-5", dto.Groups.Single(g => g.Key == "ProductKnowledge").Providers.Single(p => p.Provider == "OpenAI").Selected);
    }

    [Fact]
    public async Task A_blank_choice_puts_the_group_back_on_the_chat_model()
    {
        var store = new MemoryStore();
        store.Rows["AiProviders:TaskModels:ProductKnowledge:Anthropic"] = "claude-sonnet-5";

        var dto = await Service(store).UpdateAsync(new UpdateAiTaskModelsRequest(new[] { new AiTaskModelChoiceRequest("ProductKnowledge", "Anthropic", null) }), null);

        Assert.Equal(string.Empty, store.Rows["AiProviders:TaskModels:ProductKnowledge:Anthropic"]);
        Assert.Null(dto.Groups.Single(g => g.Key == "ProductKnowledge").Providers.Single(p => p.Provider == "Anthropic").Selected);
    }

    [Theory]
    [InlineData("Payroll", "Anthropic", "m")]
    [InlineData("ProductKnowledge", "Perplexity", "m")]
    [InlineData("ProductKnowledge", "Anthropic", "two words")]
    public async Task Refuses_an_unknown_group_or_provider_or_a_malformed_model_and_saves_nothing(string group, string provider, string model)
    {
        var store = new MemoryStore();

        await Assert.ThrowsAsync<ValidationException>(() => Service(store).UpdateAsync(new UpdateAiTaskModelsRequest(new[]
        {
            new AiTaskModelChoiceRequest("CustomerReplies", "Anthropic", "claude-haiku-4-5"), // a good one alongside must not be saved either
            new AiTaskModelChoiceRequest(group, provider, model),
        }), null));

        Assert.Empty(store.Rows);
    }

    // ---- the call really runs on the group's model ----

    private sealed class Recording : HttpMessageHandler
    {
        public JsonObject? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var body = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }),
                ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 }
            }.ToJsonString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_product_knowledge_task_is_sent_to_the_model_chosen_for_it()
    {
        var handler = new Recording();
        var credentials = Credentials(new() { [$"{AiModelGroups.ProductKnowledge}/Anthropic"] = "claude-sonnet-5" });
        var client = new AiCompletionClient(new HttpClient(handler), new FixedConfig(credentials), NullLogger<AiCompletionClient>.Instance);

        await client.CompleteAsync(new AiCompletionRequest("Anthropic", "sys", "user", 100, false, 30, Task: AiTaskKinds.DocumentAnalysis));

        Assert.Equal("claude-sonnet-5", (string?)handler.Sent!["model"]);
    }

    [Fact]
    public async Task A_check_with_no_task_still_tests_the_chat_model()
    {
        var handler = new Recording();
        var credentials = Credentials(new() { [$"{AiModelGroups.ProductKnowledge}/Anthropic"] = "claude-sonnet-5" });
        var client = new AiCompletionClient(new HttpClient(handler), new FixedConfig(credentials), NullLogger<AiCompletionClient>.Instance);

        await client.CompleteAsync(new AiCompletionRequest("Anthropic", "sys", "user", 100, false, 30));

        Assert.Equal("claude-haiku-4-5-20251001", (string?)handler.Sent!["model"]);
    }

    [Fact]
    public async Task A_keyword_suggestion_is_sent_to_the_model_chosen_for_keywords()
    {
        var handler = new Recording();
        var credentials = Credentials(new() { [$"{AiModelGroups.KeywordsAndRewrites}/Anthropic"] = "claude-haiku-4-5" });
        var generator = new AiTextGenerator(new HttpClient(handler), new FixedConfig(credentials), NullLogger<AiTextGenerator>.Instance);

        using (AiCallLabel.Use(AiTaskKinds.BusinessKeywordSuggestion))
            await generator.GenerateAsync("sys", "user", 100, provider: "Anthropic");

        Assert.Equal("claude-haiku-4-5", (string?)handler.Sent!["model"]);
    }
}
