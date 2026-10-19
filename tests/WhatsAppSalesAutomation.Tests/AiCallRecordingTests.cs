using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The recording wrappers around the existing AI features: a live call is recorded against its AI use with provider, model, time and cost; a
/// simulated or unlabelled call is not; a failure is recorded as one; embeddings are throttled; nothing stored carries a prompt or a key; and a
/// failure to record never reaches the feature. Provider calls are stubbed HTTP; nothing here contacts a real provider.
/// </summary>
[Collection("AiCallRecorder throttle")]
public sealed class AiCallRecordingTests
{
    private sealed class FakeConfig : IPlatformAiConfigProvider
    {
        public AiCredentials Credentials { get; set; } = Make("OpenAI", "OpenAI");

        public AiCredentials Get() => Credentials;
    }

    private sealed class FakeLog : IAiExecutionLog
    {
        public List<AiExecution> Rows { get; } = new();

        public bool Throw { get; set; }

        public Task WriteAsync(AiExecution execution, CancellationToken cancellationToken = default)
        {
            if (Throw)
                throw new InvalidOperationException("database down");
            Rows.Add(execution);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePolicy : IAiTaskPolicyProvider
    {
        public AiTaskPolicy Get() => new(false, Array.Empty<string>(), "Success", 0m, 60, 0, 0,
            new Dictionary<string, ProviderPrice>(StringComparer.OrdinalIgnoreCase) { ["OpenAI"] = new(1m, 2m) });
    }

    private sealed class FakeTenant : ITenantContext
    {
        public Guid? TenantId { get; } = Guid.NewGuid();
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = """{"choices":[{"message":{"content":"[\"a\"]"}}]}""";
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private static AiCredentials Make(string provider, string embedding, string? openAiKey = "sk-secret-openai-9999") => new(
        provider, embedding,
        "anthropic-key", "claude-test", "2023-06-01", "https://anthropic.test/v1",
        openAiKey, "gpt-test", "embed-test", "https://openai.test/v1",
        "google-key", "gemini-test", "embed-g", "https://google.test/v1beta");

    private readonly StubHandler _http = new();
    private readonly FakeConfig _config = new();
    private readonly FakeLog _log = new();
    private readonly FakeTenant _tenant = new();

    public AiCallRecordingTests() => AiCallRecorder.ClearThrottle();

    private AiCallRecorder Recorder() => new(_log, new FakePolicy(), _tenant, NullLogger<AiCallRecorder>.Instance);

    private RecordingAiTextGenerator TextGenerator() =>
        new(new AiTextGenerator(new HttpClient(_http), _config, NullLogger<AiTextGenerator>.Instance), _config, Recorder());

    private RecordingEmbeddingService Embedder() => new(
        new TenantEmbeddingService(_config, new OpenAiEmbeddingClient(new HttpClient(_http), NullLogger<OpenAiEmbeddingClient>.Instance),
            new GoogleEmbeddingClient(new HttpClient(_http), NullLogger<GoogleEmbeddingClient>.Instance), new SimulatedEmbeddingClient()),
        _config, Recorder());

    // ----- one-shot text -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_labelled_live_call_is_recorded_against_its_use_with_provider_model_and_tenant()
    {
        string? reply;
        using (AiCallLabel.Use(AiTaskKinds.BusinessKeywordSuggestion))
            reply = await TextGenerator().GenerateAsync("system prompt text", "user prompt text", 100);

        Assert.Equal("""["a"]""", reply); // the feature gets exactly what it got before
        var row = Assert.Single(_log.Rows);
        Assert.Equal(AiTaskKinds.BusinessKeywordSuggestion, row.Task);
        Assert.Equal("OpenAI", row.Provider);
        Assert.Equal("gpt-test", row.Model);
        Assert.Equal(AiExecutionMode.Live, row.Mode);
        Assert.Equal(AiExecutionStatus.Succeeded, row.Status);
        Assert.Equal(_tenant.TenantId, row.TenantId);
    }

    [Fact]
    public async Task A_forced_provider_is_the_one_recorded()
    {
        _http.Body = """{"candidates":[{"content":{"parts":[{"text":"ok"}]}}]}""";

        using (AiCallLabel.Use(AiTaskKinds.DiscoveryKeywordSuggestion))
            await TextGenerator().GenerateAsync("s", "u", 100, provider: "Google");

        Assert.Equal("Google", Assert.Single(_log.Rows).Provider);
    }

    [Fact]
    public async Task An_unlabelled_call_or_a_simulated_provider_is_not_recorded()
    {
        await TextGenerator().GenerateAsync("s", "u", 100);

        _config.Credentials = Make("Simulated", "Simulated");
        using (AiCallLabel.Use(AiTaskKinds.DescriptionRefinement))
            await TextGenerator().GenerateAsync("s", "u", 100);

        // A provider whose key is not saved (or is switched off, which hides the key) makes no call, so there is nothing to record.
        _config.Credentials = Make("OpenAI", "OpenAI", openAiKey: null);
        using (AiCallLabel.Use(AiTaskKinds.DescriptionRefinement))
            await TextGenerator().GenerateAsync("s", "u", 100);

        Assert.Empty(_log.Rows);
    }

    [Fact]
    public async Task A_failed_call_is_recorded_as_failed_and_the_feature_still_gets_its_quiet_null()
    {
        _http.Status = HttpStatusCode.InternalServerError;

        string? reply;
        using (AiCallLabel.Use(AiTaskKinds.DescriptionRefinement))
            reply = await TextGenerator().GenerateAsync("s", "u", 100);

        Assert.Null(reply);
        var row = Assert.Single(_log.Rows);
        Assert.Equal(AiExecutionStatus.Failed, row.Status);
        Assert.Equal(AiErrorCodes.ProviderError, row.ErrorCode);
    }

    [Fact]
    public async Task Nothing_recorded_carries_the_prompt_the_reply_or_the_key()
    {
        using (AiCallLabel.Use(AiTaskKinds.BusinessKeywordSuggestion))
            await TextGenerator().GenerateAsync("system prompt text", "user prompt text", 100);

        var stored = JsonSerializer.Serialize(_log.Rows);
        Assert.DoesNotContain("prompt text", stored);
        Assert.DoesNotContain("sk-secret-openai-9999", stored);
        Assert.DoesNotContain("[\\u0022a\\u0022]", stored);
    }

    [Fact]
    public async Task A_failure_to_record_never_reaches_the_feature()
    {
        _log.Throw = true;

        string? reply;
        using (AiCallLabel.Use(AiTaskKinds.BusinessKeywordSuggestion))
            reply = await TextGenerator().GenerateAsync("s", "u", 100);

        Assert.Equal("""["a"]""", reply);
    }

    // ----- embeddings ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Embeddings_are_recorded_but_throttled_so_a_busy_knowledge_base_does_not_flood_the_log()
    {
        _http.Body = """{"data":[{"embedding":[0.1,0.2,0.3]}]}""";
        var embedder = Embedder();

        for (var i = 0; i < 5; i++)
            Assert.Equal(3, (await embedder.GetEmbeddingAsync("chunk " + i)).Length);

        Assert.Equal(5, _http.Calls);
        var row = Assert.Single(_log.Rows);
        Assert.Equal(AiTaskKinds.KnowledgeEmbedding, row.Task);
        Assert.Equal("embed-test", row.Model);
        Assert.Equal(AiExecutionStatus.Succeeded, row.Status);
    }

    [Fact]
    public async Task An_empty_embedding_is_a_failed_call()
    {
        _http.Status = HttpStatusCode.Unauthorized;

        var vector = await Embedder().GetEmbeddingAsync("chunk");

        Assert.Empty(vector); // unchanged for the knowledge base: "nothing found"
        Assert.Equal(AiExecutionStatus.Failed, Assert.Single(_log.Rows).Status);
    }

    [Fact]
    public async Task Simulated_embeddings_are_not_recorded()
    {
        _config.Credentials = Make("OpenAI", "Simulated");

        await Embedder().GetEmbeddingAsync("chunk");

        Assert.Empty(_log.Rows);
        Assert.Equal(0, _http.Calls);
    }

    // ----- the recorder ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cost_is_estimated_from_the_provider_price_when_tokens_are_known()
    {
        await Recorder().RecordAsync(AiTaskKinds.ConversationReply, AiFeatures.Conversation, "OpenAI", "gpt-test", true, 900, 1000, 500);

        var row = Assert.Single(_log.Rows);
        Assert.Equal(0.002m, row.EstimatedCostUsd); // 1000 in * $1/M + 500 out * $2/M
        Assert.Equal(AiFeatures.Conversation, row.Feature);
        Assert.Null(row.ErrorSummary);
    }

    [Fact]
    public async Task A_failure_reason_is_scrubbed_of_anything_key_shaped()
    {
        await Recorder().RecordAsync(AiTaskKinds.LeadDiscovery, AiFeatures.DiscoveryLead, "Anthropic", null, false, 10, errorSummary: "rejected sk-ant-abcdefghijklmnopqrstuvwxyz");

        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", Assert.Single(_log.Rows).ErrorSummary);
    }
}

[CollectionDefinition("AiCallRecorder throttle", DisableParallelization = true)]
public sealed class AiCallRecorderThrottleCollection
{
}
