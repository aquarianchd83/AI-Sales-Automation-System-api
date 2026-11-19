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

    private static PlatformAiTaskModelsService Service(MemoryStore store, AiCredentials? credentials = null, AiPricingOptions? pricing = null) =>
        new(store, new FixedConfig(credentials ?? Credentials()), new FixedSnapshot<AiPricingOptions>(pricing ?? new AiPricingOptions()));

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

        Assert.Equal(new[] { "CustomerReplies", "ProductKnowledge", "KeywordsAndRewrites" }, dto.Groups.Select(g => g.Key));
        Assert.All(dto.Groups.SelectMany(g => g.Providers), cell =>
        {
            Assert.Null(cell.Selected);
            Assert.Equal(cell.ChatModel, cell.EffectiveModel);
        });
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

        Assert.False(gemini.HasPrice);
        Assert.Null(gemini.InputPer1M);
        Assert.Contains(gemini.Options, o => o.Model == "gemini-flash-lite-latest" && !o.HasPrice);
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
