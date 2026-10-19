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
using WhatsAppSalesAutomation.Infrastructure.Integrations;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The product-knowledge AI task runner: provider choice, on/off, capability, fallback, retries, the simulator's labelling, and the execution
/// log. Every provider call here is a scripted fake - these are code-level tests, not live checks of any provider.
/// </summary>
public sealed class AiTaskRunnerTests
{
    // ----- fakes -----------------------------------------------------------------------------------------------------------------------

    private sealed class FakeRouting : IAiFeatureRouting
    {
        public AiFeatureChoice Choice { get; set; } = new(true, "OpenAI");

        public AiFeatureChoice Resolve(string feature) => Choice;
    }

    private sealed class FakeCredentials : IPlatformAiConfigProvider
    {
        public AiCredentials Value { get; set; } = Creds();

        public AiCredentials Get() => Value;
    }

    private sealed class FakePolicy : IAiTaskPolicyProvider
    {
        public AiTaskPolicy Value { get; set; } = Policy();

        public AiTaskPolicy Get() => Value;
    }

    private sealed class FakeLog : IAiExecutionLog
    {
        public List<AiExecution> Rows { get; } = new();

        public bool Throw { get; set; }

        public Task WriteAsync(AiExecution execution, CancellationToken cancellationToken = default)
        {
            if (Throw)
                throw new InvalidOperationException("log is down");
            Rows.Add(execution);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedClient : IAiCompletionClient
    {
        private readonly Dictionary<string, Queue<AiCompletionResult>> _script = new(StringComparer.OrdinalIgnoreCase);

        public List<AiCompletionRequest> Calls { get; } = new();

        public ScriptedClient On(string provider, params AiCompletionResult[] results)
        {
            _script[provider] = new Queue<AiCompletionResult>(results);
            return this;
        }

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            if (!_script.TryGetValue(request.Provider, out var queue) || queue.Count == 0)
                throw new InvalidOperationException($"Unexpected call to {request.Provider}");
            return Task.FromResult(queue.Count == 1 ? queue.Peek() : queue.Dequeue());
        }
    }

    private const string GoodJson = """{"answer":"Rs 500","evidence":[{"passageId":"p1","quote":"fee is Rs 500"}]}""";

    private static AiCompletionResult Ok(string text = GoodJson, string model = "model-x", int input = 1000, int output = 200) =>
        new(true, text, model, input, output, 120, null, null, 200);

    private static AiCompletionResult Err(string code, string message = "failed", int? status = null) =>
        new(false, null, null, null, null, 50, code, message, status);

    private static AiCredentials Creds(
        bool openAi = true, bool anthropic = true, bool google = true, bool openAiKey = true, bool anthropicKey = true, bool googleKey = true) => new(
        "Simulated", "Simulated", anthropicKey ? "ant-key-1234" : null, "claude-model", "2023-06-01", "https://anthropic.test/v1/",
        openAiKey ? "oai-key-1234" : null, "gpt-model", "emb", "https://openai.test/v1/",
        googleKey ? "goog-key-1234" : null, "gemini-model", "emb", "https://google.test/v1beta/",
        anthropic, openAi, google);

    private static AiTaskPolicy Policy(
        bool fallback = false, string[]? order = null, string scenario = "Success", bool scanned = false, decimal maxCost = 0m, int retries = 0,
        decimal inPrice = 1m, decimal outPrice = 2m) => new(
        fallback, order ?? new[] { "OpenAI", "Google", "Anthropic" }, scenario, scanned, maxCost, 60, retries, 0,
        new Dictionary<string, ProviderPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = new(inPrice, outPrice), ["Anthropic"] = new(inPrice, outPrice), ["Google"] = new(inPrice, outPrice)
        });

    private readonly FakeRouting _routing = new();
    private readonly FakeCredentials _credentials = new();
    private readonly FakePolicy _policy = new();
    private readonly FakeLog _log = new();
    private readonly ScriptedClient _client = new();

    private AiTaskRunner Runner() => new(_routing, _credentials, _client, _log, _policy, NullLogger<AiTaskRunner>.Instance);

    private static AiTaskRequest Request(string task = AiTaskKinds.GroundedQuestionAnswering, bool idempotent = true, IReadOnlyCollection<string>? allowed = null, bool json = true) =>
        new(task, "system", "user", 500, json, Guid.NewGuid(), AllowedProviders: allowed, Idempotent: idempotent);

    // ----- the simulator ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_simulator_is_used_only_when_chosen_and_its_result_says_so_everywhere()
    {
        _routing.Choice = new AiFeatureChoice(false, "OpenAI");

        var result = await Runner().RunAsync(Request());

        Assert.True(result.Success);
        Assert.True(result.IsSimulated);
        Assert.Equal(AiExecutionMode.Simulator, result.Mode);
        Assert.Equal("Simulator", result.Provider);
        Assert.Empty(_client.Calls); // no provider was contacted
        var row = Assert.Single(_log.Rows);
        Assert.Equal(AiExecutionMode.Simulator, row.Mode);
        Assert.Equal("Simulator", row.Provider);

        using var doc = JsonDocument.Parse(result.Text!);
        Assert.True(doc.RootElement.GetProperty("simulated").GetBoolean());
        Assert.Contains(SimulatedTaskEngine.Marker, doc.RootElement.GetProperty("answer").GetString());
        Assert.Empty(doc.RootElement.GetProperty("evidence").EnumerateArray()); // it never claims to have read a source
    }

    [Theory]
    [InlineData("MissingEvidence", true, null)]
    [InlineData("Conflict", true, null)]
    public async Task The_simulator_can_play_missing_evidence_and_conflicting_sources(string scenario, bool success, string? code)
    {
        _routing.Choice = new AiFeatureChoice(false, "OpenAI");
        _policy.Value = Policy(scenario: scenario);

        var result = await Runner().RunAsync(Request());

        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.ErrorCode);
        using var doc = JsonDocument.Parse(result.Text!);
        if (scenario == "MissingEvidence")
        {
            Assert.True(doc.RootElement.GetProperty("insufficientEvidence").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("answer").ValueKind);
        }
        else
        {
            var conflict = Assert.Single(doc.RootElement.GetProperty("conflicts").EnumerateArray());
            Assert.Equal(2, conflict.GetProperty("values").GetArrayLength());
        }
    }

    [Fact]
    public async Task A_malformed_simulated_reply_is_treated_exactly_like_a_malformed_real_one()
    {
        _routing.Choice = new AiFeatureChoice(false, "OpenAI");
        _policy.Value = Policy(scenario: "Malformed");

        var result = await Runner().RunAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiErrorCodes.MalformedResponse, result.ErrorCode);
        Assert.Null(result.Text);
        Assert.Equal(AiExecutionStatus.Failed, Assert.Single(_log.Rows).Status);
    }

    [Fact]
    public async Task The_simulator_can_play_a_provider_error()
    {
        _routing.Choice = new AiFeatureChoice(false, "OpenAI");
        _policy.Value = Policy(scenario: "ProviderError");

        var result = await Runner().RunAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiErrorCodes.SimulatedProviderError, result.ErrorCode);
        Assert.True(result.IsSimulated);
    }

    [Fact]
    public async Task A_plain_text_task_gets_a_labelled_plain_text_simulator_reply()
    {
        _routing.Choice = new AiFeatureChoice(false, "OpenAI");

        var result = await Runner().RunAsync(Request(json: false));

        Assert.True(result.Success);
        Assert.StartsWith(SimulatedTaskEngine.Marker, result.Text);
    }

    // ----- live providers, on/off and keys -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_live_run_uses_the_chosen_provider_and_records_tokens_model_and_estimated_cost()
    {
        _client.On("OpenAI", Ok(model: "gpt-served", input: 2_000_000, output: 1_000_000));

        var result = await Runner().RunAsync(Request());

        Assert.True(result.Success);
        Assert.Equal(AiExecutionMode.Live, result.Mode);
        Assert.False(result.IsSimulated);
        Assert.Equal("OpenAI", result.Provider);
        Assert.Equal("gpt-served", result.Model);
        Assert.False(result.UsedFallback);
        Assert.Equal(4m, result.CostUsd); // 2M * $1 + 1M * $2 per million
        var row = Assert.Single(_log.Rows);
        Assert.Equal(AiExecutionMode.Live, row.Mode);
        Assert.Equal(2_000_000, row.InputTokens);
        Assert.Equal(4m, row.EstimatedCostUsd);
        Assert.Equal(AiTaskKinds.GroundedQuestionAnswering, row.Task);
    }

    [Fact]
    public async Task A_provider_that_is_switched_off_is_never_called_and_the_simulator_does_not_stand_in()
    {
        _credentials.Value = Creds(openAi: false);

        var result = await Runner().RunAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiExecutionMode.Live, result.Mode); // still a live attempt that failed - not quietly simulated
        Assert.Equal(AiErrorCodes.ProviderUnavailable, result.ErrorCode);
        Assert.Empty(_client.Calls);
        Assert.Equal(AiErrorCodes.ProviderUnavailable, Assert.Single(_log.Rows).ErrorCode);
    }

    [Fact]
    public async Task A_provider_with_no_key_is_never_called()
    {
        _credentials.Value = Creds(openAiKey: false);

        var result = await Runner().RunAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("No OpenAI key", result.ErrorMessage);
        Assert.Empty(_client.Calls);
    }

    // ----- fallback ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_fallback_a_failure_stays_a_failure()
    {
        _client.On("OpenAI", Err(AiErrorCodes.ProviderError, "boom", 500)).On("Google", Ok());

        var result = await Runner().RunAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(new[] { "OpenAI" }, _client.Calls.Select(c => c.Provider));
    }

    [Fact]
    public async Task With_fallback_the_next_provider_in_the_configured_order_is_tried_and_the_switch_is_recorded()
    {
        _policy.Value = Policy(fallback: true, order: new[] { "Google", "Anthropic" });
        _client.On("OpenAI", Err(AiErrorCodes.ProviderError, "boom", 500)).On("Google", Ok(model: "gemini-served")).On("Anthropic", Ok());

        var result = await Runner().RunAsync(Request());

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal("Google", result.Provider);
        Assert.Equal(AiExecutionMode.Live, result.Mode);
        Assert.Equal(new[] { "OpenAI", "Google" }, _client.Calls.Select(c => c.Provider)); // Anthropic never needed
        Assert.Equal(2, _log.Rows.Count);
        Assert.Equal(AiExecutionStatus.Failed, _log.Rows[0].Status);
        Assert.Equal("OpenAI", _log.Rows[1].FallbackFromProvider);
        Assert.Equal("OpenAI", _log.Rows[1].RequestedProvider);
        Assert.Equal(1, _log.Rows.Select(r => r.CorrelationId).Distinct().Count());
    }

    [Fact]
    public async Task Fallback_skips_providers_that_are_off_or_have_no_key_and_says_why()
    {
        _policy.Value = Policy(fallback: true, order: new[] { "Google", "Anthropic" });
        _credentials.Value = Creds(google: false);
        _client.On("OpenAI", Err(AiErrorCodes.ProviderError, "boom", 500)).On("Anthropic", Ok());

        var result = await Runner().RunAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("Anthropic", result.Provider);
        Assert.Contains(result.Attempts, a => a.Provider == "Google" && a.ErrorCode == AiErrorCodes.ProviderUnavailable);
        Assert.DoesNotContain(_client.Calls, c => c.Provider == "Google");
    }

    [Fact]
    public async Task When_every_provider_fails_the_task_fails_live_and_never_falls_back_to_the_simulator()
    {
        _policy.Value = Policy(fallback: true);
        _client.On("OpenAI", Err(AiErrorCodes.ProviderError, "boom", 500)).On("Google", Err(AiErrorCodes.Unauthorized)).On("Anthropic", Err(AiErrorCodes.RateLimited));

        var result = await Runner().RunAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiExecutionMode.Live, result.Mode);
        Assert.False(result.IsSimulated);
        Assert.DoesNotContain(_log.Rows, r => r.Mode == AiExecutionMode.Simulator);
        Assert.DoesNotContain(result.Attempts, a => a.Provider == "Simulator");
    }

    [Fact]
    public async Task A_call_that_must_not_be_repeated_is_neither_retried_nor_sent_to_another_provider()
    {
        _policy.Value = Policy(fallback: true, retries: 2);
        _client.On("OpenAI", Err(AiErrorCodes.Timeout)).On("Google", Ok());

        var result = await Runner().RunAsync(Request(idempotent: false));

        Assert.False(result.Success);
        Assert.Single(_client.Calls);
    }

    [Fact]
    public async Task A_temporary_failure_is_retried_on_the_same_provider_but_a_bad_key_is_not()
    {
        _policy.Value = Policy(retries: 2);
        _client.On("OpenAI", Err(AiErrorCodes.RateLimited, "slow down", 429), Err(AiErrorCodes.RateLimited, "slow down", 429), Ok());

        var retried = await Runner().RunAsync(Request());

        Assert.True(retried.Success);
        Assert.Equal(3, _client.Calls.Count);
        Assert.Equal(3, _log.Rows.Count);
        Assert.Equal(new[] { 1, 2, 3 }, _log.Rows.Select(r => r.Attempt));

        _client.Calls.Clear();
        _client.On("OpenAI", Err(AiErrorCodes.Unauthorized, "rejected", 401), Ok());
        var rejected = await Runner().RunAsync(Request());

        Assert.False(rejected.Success);
        Assert.Single(_client.Calls);
    }

    [Fact]
    public async Task A_reply_that_is_not_the_json_asked_for_counts_as_a_failure_and_can_fall_back()
    {
        _policy.Value = Policy(fallback: true, order: new[] { "Google" });
        _client.On("OpenAI", Ok("Sure! Here is the answer in prose, no JSON.")).On("Google", Ok());

        var result = await Runner().RunAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("Google", result.Provider);
        Assert.Equal(AiErrorCodes.MalformedResponse, _log.Rows[0].ErrorCode);
        Assert.Equal(AiExecutionStatus.Failed, _log.Rows[0].Status); // a fluent non-answer is not recorded as a success
    }

    [Fact]
    public async Task Json_wrapped_in_a_code_fence_is_accepted_and_cleaned()
    {
        _client.On("OpenAI", Ok("Here you go:\n```json\n" + GoodJson + "\n```"));

        var result = await Runner().RunAsync(Request());

        Assert.True(result.Success);
        Assert.Equal(GoodJson, result.Text);
    }

    // ----- permission, cost, capability -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_the_tenant_has_not_permitted_is_never_used_even_as_a_fallback()
    {
        _policy.Value = Policy(fallback: true, order: new[] { "Google", "Anthropic" });
        _client.On("Google", Ok());

        var result = await Runner().RunAsync(Request(allowed: new[] { "Google" }));

        Assert.True(result.Success);
        Assert.Equal("Google", result.Provider);
        Assert.Contains(result.Attempts, a => a.Provider == "OpenAI" && a.ErrorCode == AiErrorCodes.ProviderNotAllowed);
        Assert.DoesNotContain(_client.Calls, c => c.Provider is "OpenAI" or "Anthropic");
    }

    [Fact]
    public async Task A_call_whose_estimated_cost_is_over_the_limit_is_skipped_without_being_made()
    {
        _policy.Value = Policy(maxCost: 0.001m, inPrice: 100m, outPrice: 100m);

        var result = await Runner().RunAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(AiErrorCodes.CostLimit, result.ErrorCode);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task Reading_scanned_documents_with_ai_is_refused_while_the_switch_is_off_and_allowed_when_on()
    {
        var scanned = new AiTaskRequest(AiTaskKinds.ScannedDocumentReading, "system", "read this", 800, true, Guid.NewGuid(), ImageBase64: "aGVsbG8=", ImageMediaType: "image/png");

        var off = await Runner().RunAsync(scanned);
        Assert.False(off.Success);
        Assert.Contains("switched off", off.ErrorMessage);
        Assert.Empty(_client.Calls);

        _policy.Value = Policy(scanned: true);
        _client.On("OpenAI", Ok());
        var on = await Runner().RunAsync(scanned);
        Assert.True(on.Success);
        Assert.Equal("aGVsbG8=", _client.Calls.Single().ImageBase64);
    }

    [Fact]
    public async Task A_task_that_is_not_a_product_knowledge_task_is_refused()
    {
        var result = await Runner().RunAsync(new AiTaskRequest("MakeCoffee", "s", "u"));

        Assert.False(result.Success);
        Assert.Equal(AiErrorCodes.CapabilityUnsupported, result.ErrorCode);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public void Every_product_task_is_declared_for_the_simulator_and_all_three_providers()
    {
        foreach (var provider in new[] { "OpenAI", "Anthropic", "Google", "Simulator" })
            Assert.Equal(AiTaskKinds.Product, AiCapabilities.DeclaredFor(provider));

        Assert.Empty(AiCapabilities.DeclaredFor("Nobody"));
        Assert.Contains(AiTaskKinds.GroundedQuestionAnswering, AiTaskKinds.Product);
        Assert.Equal(12, AiTaskKinds.Product.Count);
    }

    // ----- the log must not hurt, or leak -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_to_write_the_log_does_not_fail_the_task()
    {
        _log.Throw = true;
        _client.On("OpenAI", Ok());

        var result = await Runner().RunAsync(Request());

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Provider_error_text_is_scrubbed_of_anything_that_looks_like_a_key_before_it_is_stored_or_returned()
    {
        _client.On("OpenAI", Err(AiErrorCodes.ProviderError, "Incorrect API key provided: sk-abcdefghijklmnop1234567890. Bearer abcdefghijklmnop12345", 400));

        var result = await Runner().RunAsync(Request());

        Assert.DoesNotContain("sk-abcdefghijklmnop", result.ErrorMessage);
        Assert.DoesNotContain("sk-abcdefghijklmnop", _log.Rows.Single().ErrorSummary);
        Assert.DoesNotContain("Bearer abcdefghijklmnop", _log.Rows.Single().ErrorSummary);
        Assert.Contains("[removed]", _log.Rows.Single().ErrorSummary);
    }

    [Fact]
    public async Task The_log_holds_no_prompt_and_no_document_text()
    {
        var request = new AiTaskRequest(AiTaskKinds.DocumentAnalysis, "SYSTEM-SECRET-INSTRUCTION", "CONFIDENTIAL-BROCHURE-TEXT", 500, true, Guid.NewGuid());
        _client.On("OpenAI", Ok());

        await Runner().RunAsync(request);

        var stored = JsonSerializer.Serialize(_log.Rows);
        Assert.DoesNotContain("SYSTEM-SECRET-INSTRUCTION", stored);
        Assert.DoesNotContain("CONFIDENTIAL-BROCHURE-TEXT", stored);
    }

    // ----- the policy read from settings -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_policy_is_read_from_the_settings_with_sane_limits_and_never_includes_the_simulator_in_the_fallback_order()
    {
        var options = new AiFeatureOptions
        {
            Policy = new AiPolicyOptions
            {
                FallbackEnabled = true, FallbackOrder = "google, Simulator ,openai;OpenAI,nonsense", SimulatorScenario = "conflict", TimeoutSeconds = 9999,
                MaxRetriesPerProvider = 99, RetryDelayMs = -5, MaxCostPerCallUsd = -1
            },
            Pricing = new AiPricingOptions { OpenAI = new AiPriceOption { InputPer1MUsd = 0.5m, OutputPer1MUsd = 1.5m } }
        };

        var policy = new AiTaskPolicyProvider(new FixedOptions<AiFeatureOptions>(options)).Get();

        Assert.True(policy.FallbackEnabled);
        Assert.Equal(new[] { "Google", "OpenAI" }, policy.FallbackOrder);
        Assert.Equal("Conflict", policy.SimulatorScenario);
        Assert.Equal(300, policy.TimeoutSeconds);
        Assert.Equal(3, policy.MaxRetriesPerProvider);
        Assert.Equal(0, policy.RetryDelayMs);
        Assert.Equal(0m, policy.MaxCostPerCallUsd);
        Assert.Equal(new ProviderPrice(0.5m, 1.5m), policy.PriceOf("OpenAI"));
        Assert.Equal(new ProviderPrice(0m, 0m), policy.PriceOf("Google"));
        Assert.Null(policy.PriceOf("Google").Cost(null, null));
    }

    [Fact]
    public void An_unknown_scenario_falls_back_to_success()
    {
        var policy = new AiTaskPolicyProvider(new FixedOptions<AiFeatureOptions>(new AiFeatureOptions { Policy = new AiPolicyOptions { SimulatorScenario = "chaos" } })).Get();

        Assert.Equal("Success", policy.SimulatorScenario);
    }

    // ----- the real HTTP client ----------------------------------------------------------------------------------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = "{}";

        public bool Hang { get; set; }

        public bool Throw { get; set; }

        public HttpRequestMessage? Last { get; private set; }

        /// <summary>The request body as sent, captured before the client disposes the request.</summary>
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Throw) throw new HttpRequestException("connection refused");
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }

    private AiCompletionClient Client(StubHandler handler) =>
        new(new HttpClient(handler), _credentials, NullLogger<AiCompletionClient>.Instance);

    private static AiCompletionRequest Call(string provider, bool json = true, string? image = null) =>
        new(provider, "sys", "user text", 300, json, 30, image, image is null ? null : "image/png");

    [Fact]
    public async Task The_client_reads_text_model_and_token_counts_from_each_providers_reply()
    {
        var anthropic = new StubHandler { Body = """{"model":"claude-served","content":[{"type":"text","text":"{\"a\":1}"}],"usage":{"input_tokens":11,"output_tokens":7}}""" };
        var a = await Client(anthropic).CompleteAsync(Call("Anthropic"));
        Assert.True(a.Success);
        Assert.Equal("{\"a\":1}", a.Text);
        Assert.Equal((11, 7, "claude-served"), (a.InputTokens, a.OutputTokens, a.Model));
        Assert.Equal("ant-key-1234", anthropic.Last!.Headers.GetValues("x-api-key").Single());

        var openAi = new StubHandler { Body = """{"model":"gpt-served","choices":[{"message":{"content":"{\"a\":2}"}}],"usage":{"prompt_tokens":21,"completion_tokens":9}}""" };
        var o = await Client(openAi).CompleteAsync(Call("OpenAI"));
        Assert.Equal((21, 9, "gpt-served"), (o.InputTokens, o.OutputTokens, o.Model));
        Assert.Equal("Bearer oai-key-1234", openAi.Last!.Headers.Authorization!.ToString());

        var google = new StubHandler { Body = """{"candidates":[{"content":{"parts":[{"text":"{\"a\":3}"}]}}],"usageMetadata":{"promptTokenCount":31,"candidatesTokenCount":5}}""" };
        var g = await Client(google).CompleteAsync(Call("Google"));
        Assert.Equal("{\"a\":3}", g.Text);
        Assert.Equal((31, 5, "gemini-model"), (g.InputTokens, g.OutputTokens, g.Model));
        Assert.Equal("goog-key-1234", google.Last!.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task The_client_sends_the_page_image_in_each_providers_own_format()
    {
        var anthropic = new StubHandler { Body = """{"content":[{"type":"text","text":"x"}]}""" };
        await Client(anthropic).CompleteAsync(Call("Anthropic", image: "QUJD"));
        Assert.Contains("\"type\":\"image\"", anthropic.LastBody);

        var openAi = new StubHandler { Body = """{"choices":[{"message":{"content":"x"}}]}""" };
        await Client(openAi).CompleteAsync(Call("OpenAI", image: "QUJD"));
        Assert.Contains("data:image/png;base64,QUJD", openAi.LastBody);

        var google = new StubHandler { Body = """{"candidates":[{"content":{"parts":[{"text":"x"}]}}]}""" };
        await Client(google).CompleteAsync(Call("Google", image: "QUJD"));
        Assert.Contains("inlineData", google.LastBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "UNAUTHORIZED")]
    [InlineData(HttpStatusCode.Forbidden, "UNAUTHORIZED")]
    [InlineData(HttpStatusCode.TooManyRequests, "RATE_LIMITED")]
    [InlineData(HttpStatusCode.InternalServerError, "PROVIDER_ERROR")]
    [InlineData(HttpStatusCode.BadRequest, "PROVIDER_ERROR")]
    public async Task The_client_turns_http_failures_into_normalised_codes_and_never_echoes_the_key(HttpStatusCode status, string code)
    {
        var handler = new StubHandler { Status = status, Body = """{"error":{"message":"Incorrect key sk-abcdefghijklmnopqrstuv was rejected"}}""" };

        var result = await Client(handler).CompleteAsync(Call("OpenAI"));

        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal((int)status, result.HttpStatus);
        Assert.DoesNotContain("sk-abcdefghijklmnop", result.ErrorMessage);
        Assert.DoesNotContain("oai-key-1234", result.ErrorMessage);
    }

    [Fact]
    public async Task The_client_reports_a_timeout_a_refused_connection_and_an_unreadable_reply_distinctly()
    {
        var timeout = await Client(new StubHandler { Hang = true }).CompleteAsync(Call("OpenAI") with { TimeoutSeconds = 5 }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(AiErrorCodes.Timeout, timeout.ErrorCode);

        var refused = await Client(new StubHandler { Throw = true }).CompleteAsync(Call("OpenAI"));
        Assert.Equal(AiErrorCodes.ProviderError, refused.ErrorCode);
        Assert.Contains("Could not reach", refused.ErrorMessage);

        var unreadable = await Client(new StubHandler { Body = "not json at all" }).CompleteAsync(Call("OpenAI"));
        Assert.Equal(AiErrorCodes.ProviderError, unreadable.ErrorCode);

        var empty = await Client(new StubHandler { Body = """{"choices":[{"message":{"content":""}}]}""" }).CompleteAsync(Call("OpenAI"));
        Assert.False(empty.Success);
    }

    [Fact]
    public async Task The_client_asks_gemini_for_json_only_when_json_is_wanted()
    {
        var json = new StubHandler { Body = """{"candidates":[{"content":{"parts":[{"text":"{}"}]}}]}""" };
        await Client(json).CompleteAsync(Call("Google", json: true));
        Assert.Contains("application/json", json.LastBody);

        var plain = new StubHandler { Body = """{"candidates":[{"content":{"parts":[{"text":"hi"}]}}]}""" };
        await Client(plain).CompleteAsync(Call("Google", json: false));
        Assert.DoesNotContain("responseMimeType", plain.LastBody);
    }
}
