using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using ValidationFailure = FluentValidation.Results.ValidationFailure;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

// ----- what the Integrations page shows -----------------------------------------------------------------------------------------------

public record AiCapabilityDto(string Task, bool Declared, bool Verified, DateTime? VerifiedAtUtc);

public record AiBenchmarkCaseDto(string Name, bool Passed, string? Note);

public record AiBenchmarkSummaryDto(
    int Passed, int Total, int AccuracyPercent, int AverageLatencyMs, decimal? TotalCostUsd, int? InputTokens, int? OutputTokens, DateTime RanAtUtc,
    string Model, IReadOnlyList<AiBenchmarkCaseDto> Cases);

/// <param name="Status">Ready (simulator), Off, NotConfigured (no key), NotVerified, Verified or Failed. Verified is only ever shown for the key and model
/// that were actually checked: changing either puts it back to NotVerified.</param>
public record AiProviderPanelDto(
    string Provider,
    string DisplayName,
    bool IsSimulator,
    bool Enabled,
    bool HasKey,
    string? KeyHint,
    string? Model,
    string Status,
    DateTime? LastCheckedAtUtc,
    string? LastCheckMessage,
    IReadOnlyList<AiCapabilityDto> Capabilities,
    decimal InputPer1MUsd,
    decimal OutputPer1MUsd,
    bool PriceSet,
    AiBenchmarkSummaryDto? Benchmark,
    bool ChosenForProductKnowledge);

public record AiPolicyDto(
    bool FallbackEnabled,
    IReadOnlyList<string> FallbackOrder,
    string SimulatorScenario,
    bool ScannedDocumentsViaAi,
    decimal MaxCostPerCallUsd,
    int TimeoutSeconds,
    int MaxRetriesPerProvider,
    IReadOnlyList<string> Scenarios);

/// <param name="ChosenMode">"Simulator" or "Api" - what product-knowledge tasks run on right now.</param>
public record AiProvidersOverviewDto(IReadOnlyList<AiProviderPanelDto> Providers, AiPolicyDto Policy, string ChosenMode, string ChosenProvider);

/// <summary>Only what is given changes. An empty <see cref="ApiKey"/> keeps the saved key; a key is never read back.</summary>
public record UpdateAiProviderRequest(bool? Enabled, string? Model, string? ApiKey, decimal? InputPer1MUsd, decimal? OutputPer1MUsd);

public record UpdateAiPolicyRequest(
    bool FallbackEnabled, IReadOnlyList<string> FallbackOrder, string SimulatorScenario, bool ScannedDocumentsViaAi, decimal MaxCostPerCallUsd);

public record AiProviderCheckResultDto(bool Success, string Message, AiProviderPanelDto Provider);

public record AiExecutionDto(
    Guid Id, Guid CorrelationId, string Task, string? Feature, string RequestedProvider, string Provider, string? Model, string Mode, string Status, int Attempt,
    string? FallbackFromProvider, int DurationMs, int? InputTokens, int? OutputTokens, decimal? EstimatedCostUsd, string? ErrorCode, string? ErrorSummary, DateTime CreatedAt);

public record AiExecutionQuery : PagedRequest
{
    public string? Provider { get; init; }

    public string? Task { get; init; }

    public string? Status { get; init; }
}

public interface IPlatformAiProvidersService
{
    Task<AiProvidersOverviewDto> GetAsync(CancellationToken cancellationToken = default);

    Task<AiProviderPanelDto> UpdateProviderAsync(string provider, UpdateAiProviderRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Checks the saved key, then makes one tiny real request with the configured model. Records the outcome; never throws for a provider problem.</summary>
    Task<AiProviderCheckResultDto> VerifyAsync(string provider, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Runs the fixed fitness test against the provider with its configured model, records the measured accuracy, latency and cost.</summary>
    Task<AiProviderPanelDto> RunBenchmarkAsync(string provider, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task<AiPolicyDto> UpdatePolicyAsync(UpdateAiPolicyRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task<PagedResult<AiExecutionDto>> ListExecutionsAsync(AiExecutionQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Integrations page's AI provider cards. It adds nothing of its own to configuration: keys, models and On/Off are the existing
/// <c>AiProviders:*</c> settings (saved through the same settings service as Configuration, so they stay masked and live), prices and policy
/// are <c>Integrations:Ai:*</c> settings. What it adds is the picture around them - status that is only "Verified" for the key and model that
/// were really checked, capabilities that are only "Verified" after a real call succeeded, a measured accuracy and cost - and an audit entry
/// for every change.
/// </summary>
public class PlatformAiProvidersService : IPlatformAiProvidersService
{
    private static readonly Regex ModelName = new(@"^[A-Za-z0-9._:\-/]{1,100}$", RegexOptions.Compiled);

    private readonly ISettingsService _settings;
    private readonly IPlatformAiConfigProvider _credentials;
    private readonly IAiTaskPolicyProvider _policy;
    private readonly IAiProviderVerifier _verifier;
    private readonly IAiCompletionClient _client;
    private readonly IAiExecutionLog _log;
    private readonly IAiFeatureRouting _routing;
    private readonly IApplicationDbContext _context;
    private readonly IPlatformAuditService _audit;

    public PlatformAiProvidersService(
        ISettingsService settings, IPlatformAiConfigProvider credentials, IAiTaskPolicyProvider policy, IAiProviderVerifier verifier, IAiCompletionClient client,
        IAiExecutionLog log, IAiFeatureRouting routing, IApplicationDbContext context, IPlatformAuditService audit)
    {
        _settings = settings;
        _credentials = credentials;
        _policy = policy;
        _verifier = verifier;
        _client = client;
        _log = log;
        _routing = routing;
        _context = context;
        _audit = audit;
    }

    // ----- reading ---------------------------------------------------------------------------------------------------------------

    public async Task<AiProvidersOverviewDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var route = _routing.Resolve(AiFeatures.ProductKnowledge);
        var panels = new List<AiProviderPanelDto> { SimulatorPanel(route) };
        foreach (var provider in AiFeatures.Providers)
            panels.Add(await BuildAsync(provider, route, cancellationToken));

        return new AiProvidersOverviewDto(
            panels, ToDto(_policy.Get()), route.UsesThirdParty ? PlatformIntegrationSettingsService.ApiMode : PlatformIntegrationSettingsService.SimulatorMode,
            route.UsesThirdParty ? route.Provider : AiCapabilities.Simulator);
    }

    private AiProviderPanelDto SimulatorPanel(AiFeatureChoice route) => new(
        AiCapabilities.Simulator, "Simulator", true, true, false, null, null, "Ready", null,
        "Returns fixed, labelled test data. It reads no documents and contacts no provider, and its answers can never be published as verified.",
        AiTaskKinds.Product.Select(t => new AiCapabilityDto(t, true, false, null)).ToList(), 0m, 0m, false, null, !route.UsesThirdParty);

    private async Task<AiProviderPanelDto> BuildAsync(string provider, AiFeatureChoice route, CancellationToken ct)
    {
        var credentials = _credentials.Get();
        var category = await _settings.GetCategoryAsync("AiProviders", ct);
        var keySetting = category.Items.FirstOrDefault(i => i.Key == $"AiProviders:{provider}:ApiKey");

        // The effective credentials hide a switched-off provider's key, so ask the saved settings whether a key exists at all.
        var enabled = credentials.IsEnabled(provider);
        var hasKey = keySetting?.HasValue ?? false;
        var model = ModelOf(credentials, provider);
        var price = _policy.Get().PriceOf(provider);

        var checkRow = await _context.AiExecutions.AsNoTracking()
            .Where(e => e.Provider == provider && e.Task == AiTaskKinds.ConnectionCheck)
            .OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct);

        var status = "NotVerified";
        string? message = null;
        if (!enabled) status = "Off";
        else if (!hasKey) status = "NotConfigured";
        else if (checkRow is not null)
        {
            message = checkRow.ErrorSummary ?? (checkRow.Status == AiExecutionStatus.Succeeded ? "The key and model worked." : null);
            var current = Fingerprint(KeyOf(credentials, provider), model);
            if (checkRow.Detail is not null && checkRow.Detail != current)
                message = "The key or model changed since the last check. Verify again.";
            else
                status = checkRow.Status == AiExecutionStatus.Succeeded ? "Verified" : "Failed";
        }

        var verified = await _context.AiExecutions.AsNoTracking()
            .Where(e => e.Provider == provider && e.Mode == AiExecutionMode.Live && e.Status == AiExecutionStatus.Succeeded && AiTaskKinds.Product.Contains(e.Task))
            .GroupBy(e => e.Task).Select(g => new { Task = g.Key, At = g.Max(e => e.CreatedAt) }).ToListAsync(ct);

        var capabilities = AiTaskKinds.Product
            .Select(t =>
            {
                var hit = verified.FirstOrDefault(v => v.Task == t);
                return new AiCapabilityDto(t, AiCapabilities.IsDeclared(provider, t), hit is not null, hit?.At);
            })
            .ToList();

        var benchmarkRow = await _context.AiExecutions.AsNoTracking()
            .Where(e => e.Provider == provider && e.Task == AiTaskKinds.Benchmark && e.Detail != null)
            .OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct);

        return new AiProviderPanelDto(
            provider, DisplayName(provider), false, enabled, hasKey, keySetting?.ValueHint, model, status, checkRow?.CreatedAt, message, capabilities,
            price.InputPer1MUsd, price.OutputPer1MUsd, price.InputPer1MUsd > 0 || price.OutputPer1MUsd > 0, ReadBenchmark(benchmarkRow),
            route.UsesThirdParty && string.Equals(route.Provider, provider, StringComparison.OrdinalIgnoreCase));
    }

    private static AiBenchmarkSummaryDto? ReadBenchmark(AiExecution? row)
    {
        if (row?.Detail is null)
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredBenchmark>(row.Detail, StoredJson);
            return stored is null
                ? null
                : new AiBenchmarkSummaryDto(
                    stored.Passed, stored.Total, stored.Total == 0 ? 0 : (int)Math.Round(100.0 * stored.Passed / stored.Total), row.DurationMs / Math.Max(1, stored.Total),
                    row.EstimatedCostUsd, row.InputTokens, row.OutputTokens, row.CreatedAt, row.Model ?? string.Empty,
                    stored.Cases.Select(c => new AiBenchmarkCaseDto(c.Name, c.Passed, c.Note)).ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ----- changing --------------------------------------------------------------------------------------------------------------

    public async Task<AiProviderPanelDto> UpdateProviderAsync(
        string provider, UpdateAiProviderRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        provider = RequireProvider(provider);
        var failures = new List<ValidationFailure>();

        if (!string.IsNullOrWhiteSpace(request.Model) && !ModelName.IsMatch(request.Model.Trim()))
            failures.Add(new ValidationFailure(nameof(request.Model), "Enter a model name using letters, digits and . _ - : / only."));
        if (request.ApiKey is { Length: > 0 } key && (key.Trim().Length is < 8 or > 400 || key.Any(char.IsWhiteSpace)))
            failures.Add(new ValidationFailure(nameof(request.ApiKey), "That does not look like an API key. Paste the key exactly, without spaces."));
        if (request.InputPer1MUsd is < 0 or > 10_000 || request.OutputPer1MUsd is < 0 or > 10_000)
            failures.Add(new ValidationFailure("Price", "Enter a price between 0 and 10,000 US dollars per million tokens."));
        if (failures.Count > 0)
            throw new ValidationException(failures);

        var providerValues = new Dictionary<string, string?>();
        var integrationValues = new Dictionary<string, string?>();
        var changed = new List<string>();

        if (request.Enabled is { } enabled)
        {
            providerValues[$"AiProviders:{provider}:Enabled"] = enabled ? "true" : "false";
            changed.Add(enabled ? "switched on" : "switched off");
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            providerValues[ModelKey(provider)] = request.Model.Trim();
            changed.Add("model");
        }

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            providerValues[$"AiProviders:{provider}:ApiKey"] = request.ApiKey.Trim();
            changed.Add("key replaced");
        }

        if (request.InputPer1MUsd is { } input)
        {
            integrationValues[$"Integrations:Ai:Pricing:{provider}:InputPer1MUsd"] = input.ToString(CultureInfo.InvariantCulture);
            changed.Add("input price");
        }

        if (request.OutputPer1MUsd is { } output)
        {
            integrationValues[$"Integrations:Ai:Pricing:{provider}:OutputPer1MUsd"] = output.ToString(CultureInfo.InvariantCulture);
            changed.Add("output price");
        }

        if (providerValues.Count > 0)
            await _settings.UpdateCategoryAsync("AiProviders", new UpdateSettingsRequest(providerValues), actorUserId, cancellationToken);
        if (integrationValues.Count > 0)
            await _settings.UpdateCategoryAsync("Integrations", new UpdateSettingsRequest(integrationValues), actorUserId, cancellationToken);

        if (changed.Count > 0)
            await _audit.LogAsync(actorUserId, actorEmail, "AiProviderUpdated", details: $"{DisplayName(provider)}: {string.Join(", ", changed)}", cancellationToken: cancellationToken);

        return await BuildAsync(provider, _routing.Resolve(AiFeatures.ProductKnowledge), cancellationToken);
    }

    public async Task<AiPolicyDto> UpdatePolicyAsync(UpdateAiPolicyRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var failures = new List<ValidationFailure>();
        var order = (request.FallbackOrder ?? Array.Empty<string>())
            .Select(p => AiCapabilities.RealProviders.FirstOrDefault(r => string.Equals(r, p?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (order.Any(p => p is null))
            failures.Add(new ValidationFailure(nameof(request.FallbackOrder), "The fallback order can only list ChatGPT, Claude and Gemini. The simulator is never a fallback."));
        if (!AiSimulatorScenarios.All.Contains(request.SimulatorScenario ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            failures.Add(new ValidationFailure(nameof(request.SimulatorScenario), "Choose one of the listed simulator scenarios."));
        if (request.MaxCostPerCallUsd is < 0 or > 1000)
            failures.Add(new ValidationFailure(nameof(request.MaxCostPerCallUsd), "Enter a limit between 0 and 1,000 US dollars (0 means no limit)."));
        if (failures.Count > 0)
            throw new ValidationException(failures);

        var scenario = AiSimulatorScenarios.All.First(s => string.Equals(s, request.SimulatorScenario, StringComparison.OrdinalIgnoreCase));
        await _settings.UpdateCategoryAsync("Integrations", new UpdateSettingsRequest(new Dictionary<string, string?>
        {
            ["Integrations:Ai:Policy:FallbackEnabled"] = request.FallbackEnabled ? "true" : "false",
            ["Integrations:Ai:Policy:FallbackOrder"] = string.Join(",", order.Select(p => p!).Distinct()),
            ["Integrations:Ai:Policy:SimulatorScenario"] = scenario,
            ["Integrations:Ai:Policy:ScannedDocumentsViaAi"] = request.ScannedDocumentsViaAi ? "true" : "false",
            ["Integrations:Ai:Policy:MaxCostPerCallUsd"] = request.MaxCostPerCallUsd.ToString(CultureInfo.InvariantCulture)
        }), actorUserId, cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, "AiPolicyUpdated",
            details: $"fallback {(request.FallbackEnabled ? "on" : "off")} ({string.Join(" > ", order.Select(p => DisplayName(p!)))}); scanned documents via AI {(request.ScannedDocumentsViaAi ? "on" : "off")}; simulator scenario {scenario}",
            cancellationToken: cancellationToken);

        return ToDto(_policy.Get());
    }

    // ----- checking --------------------------------------------------------------------------------------------------------------

    public async Task<AiProviderCheckResultDto> VerifyAsync(string provider, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        provider = RequireProvider(provider);
        var credentials = _credentials.Get();
        var policy = _policy.Get();
        var model = ModelOf(credentials, provider);
        var fingerprint = Fingerprint(KeyOf(credentials, provider), model);
        var route = _routing.Resolve(AiFeatures.ProductKnowledge);

        var keyCheck = (await _verifier.VerifyAsync(provider, cancellationToken)).FirstOrDefault();
        var success = keyCheck is { Success: true };
        var message = keyCheck?.Message ?? $"{DisplayName(provider)} could not be checked.";
        int? inputTokens = null, outputTokens = null;
        var duration = 0;

        if (success)
        {
            // The key opening the door is not enough: the configured model has to answer too.
            var call = await _client.CompleteAsync(new AiCompletionRequest(provider, "Reply with the single word OK.", "Say OK.", 20, false, policy.TimeoutSeconds), cancellationToken);
            duration = call.DurationMs;
            inputTokens = call.InputTokens;
            outputTokens = call.OutputTokens;
            success = call.Success;
            message = call.Success
                ? $"{DisplayName(provider)} accepted the key and the model {call.Model ?? model} answered."
                : $"The key was accepted, but the model '{model}' did not answer: {call.ErrorMessage ?? "no reason given"}";
        }

        await _log.WriteAsync(new AiExecution
        {
            CorrelationId = Guid.NewGuid(),
            Task = AiTaskKinds.ConnectionCheck,
            RequestedProvider = provider,
            Provider = provider,
            Model = model,
            Mode = AiExecutionMode.Live,
            Status = success ? AiExecutionStatus.Succeeded : AiExecutionStatus.Failed,
            DurationMs = duration,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCostUsd = policy.PriceOf(provider).Cost(inputTokens, outputTokens),
            ErrorCode = success ? null : "CHECK_FAILED",
            ErrorSummary = AiLogScrubber.Scrub(message),
            Detail = fingerprint
        }, cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, "AiProviderVerified", details: $"{DisplayName(provider)}: {(success ? "OK" : "failed")}", cancellationToken: cancellationToken);
        return new AiProviderCheckResultDto(success, AiLogScrubber.Scrub(message) ?? string.Empty, await BuildAsync(provider, route, cancellationToken));
    }

    public async Task<AiProviderPanelDto> RunBenchmarkAsync(string provider, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        provider = RequireProvider(provider);
        var credentials = _credentials.Get();
        var policy = _policy.Get();
        var route = _routing.Resolve(AiFeatures.ProductKnowledge);

        if (!credentials.IsEnabled(provider))
            throw new ValidationException(new[] { new ValidationFailure("Provider", $"{DisplayName(provider)} is switched off.") });
        if (string.IsNullOrWhiteSpace(KeyOf(credentials, provider)))
            throw new ValidationException(new[] { new ValidationFailure("Provider", $"No {DisplayName(provider)} key is saved yet.") });

        var results = new List<AiBenchmark.CaseResult>();
        int totalIn = 0, totalOut = 0, totalMs = 0;
        var anyTokens = false;
        string? model = ModelOf(credentials, provider);

        foreach (var testCase in AiBenchmark.Cases)
        {
            var call = await _client.CompleteAsync(
                new AiCompletionRequest(provider, AiBenchmark.System, AiBenchmark.UserPrompt(testCase), 300, true, policy.TimeoutSeconds), cancellationToken);
            totalMs += call.DurationMs;
            model = call.Model ?? model;
            if (call.InputTokens is { } i) { totalIn += i; anyTokens = true; }
            if (call.OutputTokens is { } o) { totalOut += o; anyTokens = true; }

            results.Add(call.Success
                ? AiBenchmark.Score(testCase, call.Text)
                : new AiBenchmark.CaseResult(testCase.Name, false, $"The call failed: {AiLogScrubber.Scrub(call.ErrorMessage, 120)}"));
        }

        var passed = results.Count(r => r.Passed);
        var detail = JsonSerializer.Serialize(new StoredBenchmark(passed, results.Count, results.Select(r => new StoredCase(r.Name, r.Passed, r.Note)).ToList()), StoredJson);

        await _log.WriteAsync(new AiExecution
        {
            CorrelationId = Guid.NewGuid(),
            Task = AiTaskKinds.Benchmark,
            RequestedProvider = provider,
            Provider = provider,
            Model = model,
            Mode = AiExecutionMode.Live,
            Status = passed > 0 ? AiExecutionStatus.Succeeded : AiExecutionStatus.Failed,
            DurationMs = totalMs,
            InputTokens = anyTokens ? totalIn : null,
            OutputTokens = anyTokens ? totalOut : null,
            EstimatedCostUsd = anyTokens ? policy.PriceOf(provider).Cost(totalIn, totalOut) : null,
            Detail = detail
        }, cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, "AiBenchmarkRun", details: $"{DisplayName(provider)}: {passed} of {results.Count} passed", cancellationToken: cancellationToken);
        return await BuildAsync(provider, route, cancellationToken);
    }

    public async Task<PagedResult<AiExecutionDto>> ListExecutionsAsync(AiExecutionQuery query, CancellationToken cancellationToken = default)
    {
        var rows = _context.AiExecutions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Provider)) rows = rows.Where(e => e.Provider == query.Provider);
        if (!string.IsNullOrWhiteSpace(query.Task)) rows = rows.Where(e => e.Task == query.Task);
        if (Enum.TryParse<AiExecutionStatus>(query.Status, true, out var status)) rows = rows.Where(e => e.Status == status);

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderByDescending(e => e.CreatedAt).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);

        return new PagedResult<AiExecutionDto>(
            page.Select(e => new AiExecutionDto(
                e.Id, e.CorrelationId, e.Task, e.Feature, e.RequestedProvider, e.Provider, e.Model, e.Mode.ToString(), e.Status.ToString(), e.Attempt, e.FallbackFromProvider,
                e.DurationMs, e.InputTokens, e.OutputTokens, e.EstimatedCostUsd, e.ErrorCode, e.ErrorSummary, e.CreatedAt)).ToList(),
            total, query.Page, query.PageSize);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private static string RequireProvider(string provider) =>
        PlatformAiFeatureSettingsService.Canonical(provider)
        ?? throw new ValidationException(new[] { new ValidationFailure("Provider", "Choose ChatGPT, Claude or Gemini.") });

    private static AiPolicyDto ToDto(AiTaskPolicy p) =>
        new(p.FallbackEnabled, p.FallbackOrder, p.SimulatorScenario, p.ScannedDocumentsViaAi, p.MaxCostPerCallUsd, p.TimeoutSeconds, p.MaxRetriesPerProvider, AiSimulatorScenarios.All);

    private static string ModelKey(string provider) => provider switch
    {
        "OpenAI" => "AiProviders:OpenAI:ChatModel",
        "Google" => "AiProviders:Google:ChatModel",
        _ => "AiProviders:Anthropic:Model"
    };

    private static string ModelOf(AiCredentials c, string provider) => provider switch
    {
        "OpenAI" => c.OpenAiChatModel,
        "Google" => c.GoogleChatModel,
        _ => c.AnthropicModel
    };

    private static string? KeyOf(AiCredentials c, string provider) => provider switch
    {
        "OpenAI" => c.OpenAiApiKey,
        "Google" => c.GoogleApiKey,
        _ => c.AnthropicApiKey
    };

    /// <summary>A short one-way fingerprint of the key and model that were checked, so "Verified" can only ever describe what was actually verified.</summary>
    private static string Fingerprint(string? key, string model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|{model}")))[..12];

    private static string DisplayName(string provider) => provider switch
    {
        "OpenAI" => "ChatGPT",
        "Anthropic" => "Claude",
        "Google" => "Gemini",
        _ => provider
    };

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    private sealed record StoredCase(string Name, bool Passed, string? Note);

    private sealed record StoredBenchmark(int Passed, int Total, List<StoredCase> Cases);
}
