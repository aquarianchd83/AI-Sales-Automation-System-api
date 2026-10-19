using System.Reflection;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Api.Controllers;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Integrations;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The Integrations page's AI provider cards: status that only says "Verified" for what was really checked, write-only keys, audited changes,
/// the measured benchmark, the execution list, and the settings the cards are made of. Provider calls are scripted fakes; nothing here
/// contacts a real provider.
/// </summary>
public sealed class PlatformAiProvidersServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);

    // ----- fakes -----------------------------------------------------------------------------------------------------------------------

    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Dictionary<string, string?> Store { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AiProviders:OpenAI:ApiKey"] = "sk-test-openai-1234",
            ["AiProviders:OpenAI:ChatModel"] = "gpt-model",
            ["AiProviders:Anthropic:ApiKey"] = null,
            ["AiProviders:Google:ApiKey"] = "goog-test-5678"
        };

        public List<(string Category, Dictionary<string, string?> Values)> Updates { get; } = new();

        public Task<IReadOnlyList<SettingCategoryDto>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<SettingCategoryDto> GetCategoryAsync(string category, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SettingItemDto> items = Store
                .Where(kv => kv.Key.StartsWith(category + ":", StringComparison.OrdinalIgnoreCase))
                .Select(kv => new SettingItemDto(kv.Key, category, kv.Key.EndsWith("ApiKey"), false, null, kv.Key.EndsWith("ApiKey") ? null : kv.Value,
                    !string.IsNullOrEmpty(kv.Value), kv.Key.EndsWith("ApiKey") && !string.IsNullOrEmpty(kv.Value) ? $"••••{kv.Value![^4..]}" : null))
                .ToList();
            return Task.FromResult(new SettingCategoryDto(category, items));
        }

        public Task UpdateCategoryAsync(string category, UpdateSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            Updates.Add((category, new Dictionary<string, string?>(request.Values)));
            foreach (var (key, value) in request.Values)
                Store[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredentials : IPlatformAiConfigProvider
    {
        public bool OpenAiOn { get; set; } = true;
        public string OpenAiKey { get; set; } = "sk-test-openai-1234";
        public string OpenAiModel { get; set; } = "gpt-model";
        public bool AnthropicOn { get; set; } = true;
        public bool GoogleOn { get; set; } = true;

        public AiCredentials Get() => new(
            "Simulated", "Simulated", null, "claude-model", "2023-06-01", "https://a.test/", OpenAiOn ? OpenAiKey : null, OpenAiModel, "emb", "https://o.test/",
            GoogleOn ? "goog-test-5678" : null, "gemini-model", "emb", "https://g.test/", AnthropicOn, OpenAiOn, GoogleOn);
    }

    private sealed class FakePolicy : IAiTaskPolicyProvider
    {
        public AiTaskPolicy Value { get; set; } = new(
            false, new[] { "OpenAI", "Google", "Anthropic" }, "Success", false, 0.5m, 60, 1, 0,
            new Dictionary<string, ProviderPrice>(StringComparer.OrdinalIgnoreCase) { ["OpenAI"] = new(1m, 2m), ["Anthropic"] = new(0m, 0m), ["Google"] = new(0m, 0m) });

        public AiTaskPolicy Get() => Value;
    }

    private sealed class FakeVerifier : IAiProviderVerifier
    {
        public bool KeyAccepted { get; set; } = true;

        public Task<IReadOnlyList<AiProviderCheckDto>> VerifyAsync(string? provider, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiProviderCheckDto>>(new[]
            {
                new AiProviderCheckDto(provider ?? "OpenAI", true, false, KeyAccepted, KeyAccepted ? "ChatGPT accepted the API key." : "ChatGPT rejected the API key (HTTP 401).")
            });
    }

    private sealed class FakeClient : IAiCompletionClient
    {
        public Func<AiCompletionRequest, AiCompletionResult> Respond { get; set; } =
            r => new AiCompletionResult(true, "OK", "gpt-served", 10, 2, 100, null, null, 200);

        public List<AiCompletionRequest> Calls { get; } = new();

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class FakeRouting : IAiFeatureRouting
    {
        public AiFeatureChoice Choice { get; set; } = new(false, "OpenAI");

        public AiFeatureChoice Resolve(string feature) => Choice;
    }

    private sealed class RecordingAudit : IPlatformAuditService
    {
        public List<(string Action, string? Details)> Entries { get; } = new();

        public Task LogAsync(Guid actorUserId, string actorEmail, string action, Guid? targetTenantId = null, Guid? targetUserId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add((action, details));
            return Task.CompletedTask;
        }

        public Task<PagedResult<PlatformAuditLogEntryDto>> GetPagedAsync(PlatformAuditLogQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    /// <summary>Writes through the same context the service reads, so a check or a benchmark is visible to the next panel build.</summary>
    private sealed class DbLog : IAiExecutionLog
    {
        private readonly SqliteApplicationDbContext _db;

        public DbLog(SqliteApplicationDbContext db) => _db = db;

        public async Task WriteAsync(AiExecution execution, CancellationToken cancellationToken = default)
        {
            _db.AiExecutions.Add(execution);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly FakeSettings _settings = new();
    private readonly FakeCredentials _credentials = new();
    private readonly FakePolicy _policy = new();
    private readonly FakeVerifier _verifier = new();
    private readonly FakeClient _client = new();
    private readonly FakeRouting _routing = new();
    private readonly RecordingAudit _audit = new();
    private readonly PlatformAiProvidersService _service;
    private readonly Guid _admin = Guid.NewGuid();

    public PlatformAiProvidersServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();

        var tick = 0;
        _db.BeforeSave = db =>
        {
            foreach (var entry in db.ChangeTracker.Entries<WhatsAppSalesAutomation.Domain.Common.BaseEntity>().Where(e => e.State == EntityState.Added))
                entry.Entity.CreatedAt = Now.AddSeconds(++tick);
        };

        _service = new PlatformAiProvidersService(_settings, _credentials, _policy, _verifier, _client, new DbLog(_db), _routing, _db, _audit);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<AiProviderPanelDto> Panel(string provider) =>
        (await _service.GetAsync()).Providers.Single(p => p.Provider == provider);

    // ----- the overview ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_overview_lists_the_simulator_and_the_three_providers_with_what_product_knowledge_runs_on()
    {
        var overview = await _service.GetAsync();

        Assert.Equal(new[] { "Simulator", "OpenAI", "Anthropic", "Google" }, overview.Providers.Select(p => p.Provider));
        Assert.Equal("Simulator", overview.ChosenMode);
        var simulator = overview.Providers[0];
        Assert.True(simulator.IsSimulator);
        Assert.Equal("Ready", simulator.Status);
        Assert.True(simulator.ChosenForProductKnowledge);
        Assert.Contains("can never be published as verified", simulator.LastCheckMessage);
        Assert.False(overview.Providers[1].ChosenForProductKnowledge);

        _routing.Choice = new AiFeatureChoice(true, "Google");
        var live = await _service.GetAsync();
        Assert.Equal("Api", live.ChosenMode);
        Assert.Equal("Google", live.ChosenProvider);
        Assert.True(live.Providers.Single(p => p.Provider == "Google").ChosenForProductKnowledge);
        Assert.False(live.Providers[0].ChosenForProductKnowledge);
    }

    [Fact]
    public async Task A_provider_without_a_key_is_not_configured_and_a_switched_off_one_is_off()
    {
        Assert.Equal("NotConfigured", (await Panel("Anthropic")).Status);

        _credentials.OpenAiOn = false;
        Assert.Equal("Off", (await Panel("OpenAI")).Status);
    }

    [Fact]
    public async Task A_provider_with_a_key_that_has_never_been_checked_says_so_and_shows_only_a_masked_key()
    {
        var panel = await Panel("OpenAI");

        Assert.Equal("NotVerified", panel.Status);
        Assert.True(panel.HasKey);
        Assert.Equal("••••1234", panel.KeyHint);
        Assert.Equal("gpt-model", panel.Model);
        Assert.Null(panel.LastCheckedAtUtc);
    }

    [Fact]
    public async Task Verify_checks_the_key_and_then_the_model_and_only_then_says_verified_with_the_time()
    {
        var result = await _service.VerifyAsync("OpenAI", _admin, "root@platform.test");

        Assert.True(result.Success);
        Assert.Contains("gpt-served answered", result.Message);
        Assert.Equal("Verified", result.Provider.Status);
        Assert.NotNull(result.Provider.LastCheckedAtUtc);
        var call = Assert.Single(_client.Calls);
        Assert.Equal("OpenAI", call.Provider);
        Assert.Contains(_audit.Entries, e => e.Action == "AiProviderVerified" && e.Details == "ChatGPT: OK");

        // The check is a real, recorded execution - with tokens and an estimated cost from the price table.
        var row = await _db.AiExecutions.SingleAsync(e => e.Task == AiTaskKinds.ConnectionCheck);
        Assert.Equal(AiExecutionMode.Live, row.Mode);
        Assert.Equal(10, row.InputTokens);
        Assert.Equal(0.000014m, row.EstimatedCostUsd);
    }

    [Fact]
    public async Task A_rejected_key_fails_the_check_without_making_a_model_call()
    {
        _verifier.KeyAccepted = false;

        var result = await _service.VerifyAsync("OpenAI", _admin, "root@platform.test");

        Assert.False(result.Success);
        Assert.Equal("Failed", result.Provider.Status);
        Assert.Contains("rejected", result.Provider.LastCheckMessage);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task A_key_that_works_with_a_model_that_does_not_is_reported_as_a_model_problem()
    {
        _client.Respond = _ => new AiCompletionResult(false, null, null, null, null, 50, AiErrorCodes.ProviderError, "The model 'gpt-model' does not exist", 404);

        var result = await _service.VerifyAsync("OpenAI", _admin, "root@platform.test");

        Assert.False(result.Success);
        Assert.Contains("model 'gpt-model' did not answer", result.Message);
        Assert.Equal("Failed", result.Provider.Status);
    }

    [Fact]
    public async Task Verified_describes_only_the_key_and_model_that_were_checked()
    {
        await _service.VerifyAsync("OpenAI", _admin, "root@platform.test");
        Assert.Equal("Verified", (await Panel("OpenAI")).Status);

        _credentials.OpenAiModel = "a-different-model";
        var afterModelChange = await Panel("OpenAI");
        Assert.Equal("NotVerified", afterModelChange.Status);
        Assert.Contains("changed since the last check", afterModelChange.LastCheckMessage);

        _credentials.OpenAiModel = "gpt-model";
        _credentials.OpenAiKey = "sk-a-new-key-9999";
        Assert.Equal("NotVerified", (await Panel("OpenAI")).Status);
    }

    [Fact]
    public async Task Nothing_stored_by_a_check_contains_the_key()
    {
        await _service.VerifyAsync("OpenAI", _admin, "root@platform.test");

        var stored = JsonSerializer.Serialize(await _db.AiExecutions.AsNoTracking().ToListAsync());
        Assert.DoesNotContain("sk-test-openai-1234", stored);
        Assert.DoesNotContain("openai-1234", stored);
        Assert.DoesNotContain("sk-test-openai-1234", string.Join("|", _audit.Entries.Select(e => e.Details)));
    }

    // ----- capabilities ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_capability_is_declared_for_every_provider_but_verified_only_after_a_real_successful_call_for_that_task()
    {
        var before = await Panel("OpenAI");
        Assert.All(before.Capabilities, c => Assert.True(c.Declared));
        Assert.All(before.Capabilities, c => Assert.False(c.Verified));
        Assert.Equal(12, before.Capabilities.Count);

        // A simulator success, a live failure and another provider's success must not count.
        _db.AiExecutions.AddRange(
            Row("OpenAI", AiTaskKinds.DocumentAnalysis, AiExecutionMode.Simulator, AiExecutionStatus.Succeeded),
            Row("OpenAI", AiTaskKinds.ProductInfoExtraction, AiExecutionMode.Live, AiExecutionStatus.Failed),
            Row("Google", AiTaskKinds.KnowledgeDraft, AiExecutionMode.Live, AiExecutionStatus.Succeeded),
            Row("OpenAI", AiTaskKinds.GroundedQuestionAnswering, AiExecutionMode.Live, AiExecutionStatus.Succeeded));
        await _db.SaveChangesAsync();

        var after = await Panel("OpenAI");
        var verified = after.Capabilities.Where(c => c.Verified).Select(c => c.Task).ToList();
        Assert.Equal(new[] { AiTaskKinds.GroundedQuestionAnswering }, verified);
        Assert.NotNull(after.Capabilities.Single(c => c.Verified).VerifiedAtUtc);
    }

    private static AiExecution Row(string provider, string task, AiExecutionMode mode, AiExecutionStatus status) => new()
    {
        CorrelationId = Guid.NewGuid(), Task = task, RequestedProvider = provider, Provider = provider, Mode = mode, Status = status
    };

    // ----- changing a provider -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Updating_a_provider_saves_through_the_existing_settings_and_audits_what_changed_without_the_key()
    {
        var panel = await _service.UpdateProviderAsync(
            "Anthropic", new UpdateAiProviderRequest(true, " claude-sonnet-x ", "ant-secret-key-0042", 3m, 15m), _admin, "root@platform.test");

        var providers = _settings.Updates.Single(u => u.Category == "AiProviders").Values;
        Assert.Equal("true", providers["AiProviders:Anthropic:Enabled"]);
        Assert.Equal("claude-sonnet-x", providers["AiProviders:Anthropic:Model"]);
        Assert.Equal("ant-secret-key-0042", providers["AiProviders:Anthropic:ApiKey"]);
        var integrations = _settings.Updates.Single(u => u.Category == "Integrations").Values;
        Assert.Equal("3", integrations["Integrations:Ai:Pricing:Anthropic:InputPer1MUsd"]);
        Assert.Equal("15", integrations["Integrations:Ai:Pricing:Anthropic:OutputPer1MUsd"]);

        var audit = Assert.Single(_audit.Entries, e => e.Action == "AiProviderUpdated");
        Assert.Contains("Claude", audit.Details);
        Assert.Contains("key replaced", audit.Details);
        Assert.DoesNotContain("ant-secret-key-0042", audit.Details);

        // The key can be written but never read back: only a masked hint exists on the panel.
        Assert.True(panel.HasKey);
        Assert.Equal("••••0042", panel.KeyHint);
        Assert.DoesNotContain("ant-secret-key-0042", JsonSerializer.Serialize(panel));
    }

    [Fact]
    public async Task An_empty_key_keeps_the_saved_one_and_a_request_with_no_changes_writes_nothing()
    {
        await _service.UpdateProviderAsync("OpenAI", new UpdateAiProviderRequest(null, null, "", null, null), _admin, "root@platform.test");
        await _service.UpdateProviderAsync("OpenAI", new UpdateAiProviderRequest(null, "  ", null, null, null), _admin, "root@platform.test");

        Assert.Empty(_settings.Updates);
        Assert.Empty(_audit.Entries);
        Assert.Equal("sk-test-openai-1234", _settings.Store["AiProviders:OpenAI:ApiKey"]);
    }

    [Theory]
    [InlineData("OpenAI", "bad model name!", null, null, "Model")]
    [InlineData("OpenAI", null, "has a space inside", null, "ApiKey")]
    [InlineData("OpenAI", null, "short", null, "ApiKey")]
    [InlineData("OpenAI", null, null, -1, "Price")]
    [InlineData("OpenAI", null, null, 20000, "Price")]
    public async Task Invalid_changes_are_refused_and_nothing_is_saved(string provider, string? model, string? key, int? price, string property)
    {
        var request = new UpdateAiProviderRequest(null, model, key, price, null);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateProviderAsync(provider, request, _admin, "root@platform.test"));

        Assert.Contains(ex.Errors, e => e.PropertyName == property);
        Assert.Empty(_settings.Updates);
    }

    [Theory]
    [InlineData("Simulator")]
    [InlineData("Cohere")]
    [InlineData("")]
    public async Task Only_the_three_real_providers_can_be_changed_checked_or_benchmarked(string provider)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateProviderAsync(provider, new UpdateAiProviderRequest(true, null, null, null, null), _admin, "a@b.c"));
        await Assert.ThrowsAsync<ValidationException>(() => _service.VerifyAsync(provider, _admin, "a@b.c"));
        await Assert.ThrowsAsync<ValidationException>(() => _service.RunBenchmarkAsync(provider, _admin, "a@b.c"));
    }

    [Fact]
    public async Task Provider_names_are_matched_without_regard_to_case()
    {
        var panel = await _service.UpdateProviderAsync("openai", new UpdateAiProviderRequest(false, null, null, null, null), _admin, "a@b.c");

        Assert.Equal("OpenAI", panel.Provider);
        Assert.Equal("false", _settings.Updates.Single().Values["AiProviders:OpenAI:Enabled"]);
    }

    [Fact]
    public async Task Prices_set_on_the_page_show_up_on_the_panel_as_set()
    {
        Assert.True((await Panel("OpenAI")).PriceSet);
        Assert.False((await Panel("Anthropic")).PriceSet);
    }

    // ----- the policy --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_policy_can_be_changed_and_is_audited()
    {
        var policy = await _service.UpdatePolicyAsync(
            new UpdateAiPolicyRequest(true, new[] { "google", "OpenAI", "google" }, "conflict", true, 1.25m), _admin, "root@platform.test");

        var saved = _settings.Updates.Single().Values;
        Assert.Equal("true", saved["Integrations:Ai:Policy:FallbackEnabled"]);
        Assert.Equal("Google,OpenAI", saved["Integrations:Ai:Policy:FallbackOrder"]);
        Assert.Equal("Conflict", saved["Integrations:Ai:Policy:SimulatorScenario"]);
        Assert.Equal("true", saved["Integrations:Ai:Policy:ScannedDocumentsViaAi"]);
        Assert.Equal("1.25", saved["Integrations:Ai:Policy:MaxCostPerCallUsd"]);
        Assert.NotNull(policy);

        var audit = Assert.Single(_audit.Entries, e => e.Action == "AiPolicyUpdated");
        Assert.Contains("Gemini > ChatGPT", audit.Details);
        Assert.Contains("scanned documents via AI on", audit.Details);
    }

    [Fact]
    public async Task The_simulator_can_never_be_put_in_the_fallback_order_and_bad_values_are_refused()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.UpdatePolicyAsync(new UpdateAiPolicyRequest(true, new[] { "OpenAI", "Simulator" }, "Chaos", false, -1m), _admin, "a@b.c"));

        var properties = ex.Errors.Select(e => e.PropertyName).ToList();
        Assert.Contains("FallbackOrder", properties);
        Assert.Contains("SimulatorScenario", properties);
        Assert.Contains("MaxCostPerCallUsd", properties);
        Assert.Empty(_settings.Updates);
    }

    // ----- benchmark ---------------------------------------------------------------------------------------------------------------------

    private static string Question(AiCompletionRequest request) => request.User[(request.User.IndexOf("QUESTION:", StringComparison.Ordinal) + 10)..].Trim();

    /// <summary>What a careful model would answer: the right fact with an exact quote, and "not stated" where the text is silent.</summary>
    private static AiCompletionResult CarefulAnswer(AiCompletionRequest request)
    {
        var q = Question(request);
        var json = q switch
        {
            "What is the consultation fee?" => """{"answer":"Rs 500","quote":"The consultation fee is Rs 500 for the first visit."}""",
            "How much does cataract surgery cost?" => """{"answer":"From Rs 25,000 per eye","quote":"Cataract surgery starts at Rs 25,000 per eye."}""",
            "Is the clinic open on Sundays?" => """{"answer":"No, it is closed on Sundays","quote":"The clinic is open Monday to Saturday, 9 am to 6 pm, and is closed on Sundays."}""",
            "Until when do senior citizens get a free check-up?" => """{"answer":"31 December","quote":"Senior citizens get a free eye check-up until 31 December."}""",
            _ => """{"answer":null,"quote":null}"""
        };
        return new AiCompletionResult(true, json, "gpt-served", 300, 40, 200, null, null, 200);
    }

    [Fact]
    public async Task The_benchmark_measures_accuracy_latency_and_cost_and_stores_the_result_on_the_card()
    {
        _client.Respond = CarefulAnswer;

        var panel = await _service.RunBenchmarkAsync("OpenAI", _admin, "root@platform.test");

        var b = panel.Benchmark!;
        Assert.Equal((6, 6, 100), (b.Passed, b.Total, b.AccuracyPercent));
        Assert.Equal(200, b.AverageLatencyMs);
        Assert.Equal(1800, b.InputTokens);
        Assert.Equal(240, b.OutputTokens);
        Assert.Equal(0.00228m, b.TotalCostUsd); // 1800 in * $1/M + 240 out * $2/M
        Assert.Equal("gpt-served", b.Model);
        Assert.Equal(6, b.Cases.Count);
        Assert.Equal(6, _client.Calls.Count);
        Assert.All(_client.Calls, c => Assert.Equal("OpenAI", c.Provider));
        Assert.Contains(_audit.Entries, e => e.Action == "AiBenchmarkRun" && e.Details == "ChatGPT: 6 of 6 passed");
    }

    [Fact]
    public async Task A_model_that_invents_answers_or_fake_quotes_scores_low()
    {
        _client.Respond = request =>
        {
            // Always confident, always quotes something plausible that is not in the text - the failure the engine most needs to catch.
            var json = """{"answer":"Rs 999","quote":"Our fee is the lowest in Pune."}""";
            return new AiCompletionResult(true, json, "gpt-served", 100, 20, 100, null, null, 200);
        };

        var b = (await _service.RunBenchmarkAsync("OpenAI", _admin, "root@platform.test")).Benchmark!;

        Assert.Equal(0, b.Passed);
        Assert.Equal(0, b.AccuracyPercent);
        Assert.All(b.Cases, c => Assert.False(c.Passed));
    }

    [Fact]
    public async Task A_provider_that_fails_every_call_scores_zero_and_the_reason_is_scrubbed()
    {
        _client.Respond = _ => new AiCompletionResult(false, null, null, null, null, 30, AiErrorCodes.Unauthorized, "bad key sk-abcdefghijklmnopqrstuv", 401);

        var b = (await _service.RunBenchmarkAsync("OpenAI", _admin, "root@platform.test")).Benchmark!;

        Assert.Equal(0, b.Passed);
        Assert.All(b.Cases, c => Assert.DoesNotContain("sk-abcdefghijklmnop", c.Note));
        Assert.Null(b.TotalCostUsd);
    }

    [Fact]
    public async Task The_newest_benchmark_is_the_one_shown()
    {
        _client.Respond = CarefulAnswer;
        await _service.RunBenchmarkAsync("OpenAI", _admin, "a@b.c");
        _client.Respond = _ => new AiCompletionResult(true, """{"answer":"x","quote":"y"}""", "gpt-served", 1, 1, 1, null, null, 200);

        var latest = (await _service.RunBenchmarkAsync("OpenAI", _admin, "a@b.c")).Benchmark!;

        Assert.Equal(0, latest.Passed);
        Assert.Equal(2, await _db.AiExecutions.CountAsync(e => e.Task == AiTaskKinds.Benchmark));
    }

    [Fact]
    public async Task A_provider_that_is_off_or_has_no_key_cannot_be_benchmarked()
    {
        _credentials.OpenAiOn = false;
        await Assert.ThrowsAsync<ValidationException>(() => _service.RunBenchmarkAsync("OpenAI", _admin, "a@b.c"));
        await Assert.ThrowsAsync<ValidationException>(() => _service.RunBenchmarkAsync("Anthropic", _admin, "a@b.c"));
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public void Benchmark_scoring_rewards_honesty_and_real_quotes_and_punishes_invention()
    {
        var fee = AiBenchmark.Cases[0];
        var lasik = AiBenchmark.Cases[4];

        Assert.True(AiBenchmark.Score(fee, """{"answer":"Rs 500","quote":"The consultation fee is Rs 500 for the first visit."}""").Passed);
        Assert.True(AiBenchmark.Score(fee, "```json\n{\"answer\":\"500 rupees\",\"quote\":\"the consultation fee is rs 500 for the first visit\"}\n```").Passed); // fence, case and full stops do not matter

        Assert.False(AiBenchmark.Score(fee, "not json").Passed);
        Assert.False(AiBenchmark.Score(fee, """{"answer":"Rs 800","quote":"The consultation fee is Rs 500 for the first visit."}""").Passed); // wrong answer
        Assert.False(AiBenchmark.Score(fee, """{"answer":"Rs 500","quote":null}""").Passed); // no citation
        Assert.False(AiBenchmark.Score(fee, """{"answer":"Rs 500","quote":"Consultation costs five hundred rupees."}""").Passed); // fabricated quote
        Assert.False(AiBenchmark.Score(fee, """{"answer":null,"quote":null}""").Passed); // says it is not stated when it is

        Assert.True(AiBenchmark.Score(lasik, """{"answer":null,"quote":null}""").Passed);
        Assert.True(AiBenchmark.Score(lasik, """{"answer":"The text does not state this.","quote":null}""").Passed);
        Assert.False(AiBenchmark.Score(lasik, """{"answer":"Yes, LASIK is offered","quote":null}""").Passed); // invented
        Assert.False(AiBenchmark.Score(lasik, """{"answer":null,"quote":"Cataract surgery starts at Rs 25,000 per eye."}""").Passed); // cites for nothing
    }

    // ----- the execution list -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Executions_are_listed_newest_first_and_can_be_filtered()
    {
        _db.AiExecutions.AddRange(
            Row("OpenAI", AiTaskKinds.DocumentAnalysis, AiExecutionMode.Live, AiExecutionStatus.Succeeded),
            Row("Google", AiTaskKinds.KnowledgeDraft, AiExecutionMode.Live, AiExecutionStatus.Failed),
            Row("OpenAI", AiTaskKinds.KnowledgeDraft, AiExecutionMode.Live, AiExecutionStatus.Failed));
        await _db.SaveChangesAsync();

        var all = await _service.ListExecutionsAsync(new AiExecutionQuery());
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(new[] { "OpenAI", "Google", "OpenAI" }, all.Items.Select(i => i.Provider));
        Assert.Equal(AiTaskKinds.KnowledgeDraft, all.Items[0].Task);

        Assert.Equal(2, (await _service.ListExecutionsAsync(new AiExecutionQuery { Provider = "OpenAI" })).TotalCount);
        Assert.Equal(2, (await _service.ListExecutionsAsync(new AiExecutionQuery { Status = "failed" })).TotalCount);
        Assert.Single((await _service.ListExecutionsAsync(new AiExecutionQuery { Task = AiTaskKinds.DocumentAnalysis })).Items);

        var page = await _service.ListExecutionsAsync(new AiExecutionQuery { Page = 2, PageSize = 2 });
        Assert.Single(page.Items);
        Assert.Equal(3, page.TotalCount);
    }

    // ----- the feature choice and the surface ----------------------------------------------------------------------------------------------

    [Fact]
    public void Product_knowledge_is_an_engine_choice_on_the_integrations_page_with_all_three_tools_and_the_simulator_by_default()
    {
        Assert.Contains(AiFeatures.ProductKnowledge, AiFeatures.All);
        Assert.Equal(new[] { "OpenAI", "Anthropic", "Google" }, AiFeatures.SupportedProviders(AiFeatures.ProductKnowledge));
        Assert.Equal(PlatformIntegrationSettingsService.SimulatorMode, AiFeatures.DefaultMode(AiFeatures.ProductKnowledge));
        Assert.Equal("Integrations:Ai:ProductKnowledge", AiFeatures.Prefix(AiFeatures.ProductKnowledge));

        var options = new AiFeatureOptions { ProductKnowledge = new AiFeatureOption { Mode = "Api", Provider = "Anthropic" } };
        var choice = new AiFeatureRouting(new FixedOptions<AiFeatureOptions>(options)).Resolve(AiFeatures.ProductKnowledge);
        Assert.True(choice.UsesThirdParty);
        Assert.Equal("Anthropic", choice.Provider);

        Assert.False(new AiFeatureRouting(new FixedOptions<AiFeatureOptions>(new AiFeatureOptions())).Resolve(AiFeatures.ProductKnowledge).UsesThirdParty);
    }

    [Fact]
    public void Every_new_setting_is_in_the_catalog_with_a_default_and_fixed_choices_are_dropdowns()
    {
        var keys = AppSettingCatalog.All.Where(d => d.Key.StartsWith("Integrations:Ai:Policy:") || d.Key.StartsWith("Integrations:Ai:Pricing:") || d.Key.StartsWith("Integrations:Ai:ProductKnowledge:")).ToList();

        Assert.Equal(8 + 6 + 2, keys.Count);
        Assert.All(keys, d => Assert.False(string.IsNullOrWhiteSpace(d.DefaultValue), d.Key));
        Assert.All(keys, d => Assert.False(d.IsSecret));
        Assert.Equal(AiSimulatorScenarios.All, keys.Single(d => d.Key.EndsWith("SimulatorScenario")).Options);
        Assert.Equal(new[] { "true", "false" }, keys.Single(d => d.Key.EndsWith("FallbackEnabled")).Options);
        Assert.Equal("false", keys.Single(d => d.Key.EndsWith("ScannedDocumentsViaAi")).DefaultValue); // the scanned-document switch starts off
        Assert.Equal("false", keys.Single(d => d.Key.EndsWith("FallbackEnabled")).DefaultValue);
    }

    [Fact]
    public void Only_a_platform_admin_reaches_the_provider_endpoints_and_no_response_can_carry_a_key()
    {
        var controller = typeof(PlatformAiProvidersController);
        Assert.Equal(AppRoles.PlatformSuperAdmin, controller.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());

        // The write side may take a key; nothing the page reads back has a property that could hold one.
        foreach (var dto in new[] { typeof(AiProviderPanelDto), typeof(AiProvidersOverviewDto), typeof(AiProviderCheckResultDto), typeof(AiPolicyDto), typeof(AiExecutionDto), typeof(AiBenchmarkSummaryDto) })
            Assert.DoesNotContain(dto.GetProperties(), p => p.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Key", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(typeof(UpdateAiProviderRequest).GetProperties(), p => p.Name == nameof(UpdateAiProviderRequest.ApiKey));
    }
}
