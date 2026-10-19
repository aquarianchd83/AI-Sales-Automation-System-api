using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Ai.Tasks;

/// <summary>
/// The single place an AI task picks its provider. Everything that needs the product-knowledge engine's AI - reading documents, drafting
/// answers, checking for conflicts - calls this instead of choosing a provider itself, so the admin's choice on the Integrations page, the
/// on/off switches, the capability check, the fallback order and the execution log apply to all of it the same way.
///
/// What it deliberately does not do: it never answers from the simulator when a live provider was chosen and failed, never retries or
/// re-sends a call that has an external side effect, and never logs a prompt, a document or a key.
/// </summary>
public sealed class AiTaskRunner : IAiTaskRunner
{
    private readonly IAiFeatureRouting _routing;
    private readonly IPlatformAiConfigProvider _credentials;
    private readonly IAiCompletionClient _client;
    private readonly IAiExecutionLog _log;
    private readonly IAiTaskPolicyProvider _policy;
    private readonly ILogger<AiTaskRunner> _logger;

    public AiTaskRunner(
        IAiFeatureRouting routing, IPlatformAiConfigProvider credentials, IAiCompletionClient client, IAiExecutionLog log,
        IAiTaskPolicyProvider policy, ILogger<AiTaskRunner> logger)
    {
        _routing = routing;
        _credentials = credentials;
        _client = client;
        _log = log;
        _policy = policy;
        _logger = logger;
    }

    public async Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
    {
        var policy = _policy.Get();
        var correlation = Guid.NewGuid();
        var route = _routing.Resolve(request.Feature);
        var attempts = new List<AiAttempt>();

        if (!AiTaskKinds.Product.Contains(request.Task, StringComparer.Ordinal))
            return Fail(request, route, correlation, attempts, AiErrorCodes.CapabilityUnsupported, $"'{request.Task}' is not a product-knowledge task.");

        if (request.Task == AiTaskKinds.ScannedDocumentReading && !policy.ScannedDocumentsViaAi)
            return Fail(request, route, correlation, attempts, AiErrorCodes.CapabilityUnsupported,
                "Reading scanned documents with AI is switched off on the Integrations page.");

        if (!route.UsesThirdParty)
            return await RunSimulatorAsync(request, policy, correlation, route, attempts, cancellationToken);

        return await RunLiveAsync(request, policy, correlation, route, attempts, cancellationToken);
    }

    // ----- simulator ---------------------------------------------------------------------------------------------------------------

    private async Task<AiTaskResult> RunSimulatorAsync(
        AiTaskRequest request, AiTaskPolicy policy, Guid correlation, AiFeatureChoice route, List<AiAttempt> attempts, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var reply = SimulatedTaskEngine.Respond(request.Task, policy.SimulatorScenario, request.ExpectJson);

        string? code = null;
        string? message = null;
        string? text = reply;

        if (reply is null)
        {
            code = AiErrorCodes.SimulatedProviderError;
            message = "The simulator was set to return a provider error.";
        }
        else if (request.ExpectJson)
        {
            if (AiJson.TryExtractObject(reply, out var json))
                text = json;
            else
            {
                code = AiErrorCodes.MalformedResponse;
                message = "The simulator returned a reply that is not valid JSON.";
                text = null;
            }
        }

        var ok = code is null;
        attempts.Add(new AiAttempt(AiCapabilities.Simulator, ok, code, message, (int)watch.ElapsedMilliseconds));
        await WriteAsync(request, correlation, route.Provider, AiCapabilities.Simulator, "simulator", AiExecutionMode.Simulator, ok, 1, null,
            (int)watch.ElapsedMilliseconds, null, null, null, code, message, ct);

        return new AiTaskResult(ok, text, AiExecutionMode.Simulator, AiCapabilities.Simulator, "simulator", false, code, message, correlation, attempts, null, null, null);
    }

    // ----- live providers ----------------------------------------------------------------------------------------------------------

    private async Task<AiTaskResult> RunLiveAsync(
        AiTaskRequest request, AiTaskPolicy policy, Guid correlation, AiFeatureChoice route, List<AiAttempt> attempts, CancellationToken ct)
    {
        // The chosen provider first. Fallbacks follow only when the admin switched them on, and never for a call that must not be repeated.
        var candidates = new List<string> { route.Provider };
        if (policy.FallbackEnabled && request.Idempotent)
            candidates.AddRange(policy.FallbackOrder.Where(p => !string.Equals(p, route.Provider, StringComparison.OrdinalIgnoreCase)));

        var credentials = _credentials.Get();
        string? failedProvider = null;
        string? lastCode = null;
        string? lastMessage = null;
        var attemptNumber = 0;

        foreach (var provider in candidates)
        {
            // A provider that cannot be used is recorded and skipped - the log says why, instead of the task failing silently.
            var skip = SkipReason(request, policy, credentials, provider);
            if (skip is not null)
            {
                attemptNumber++;
                attempts.Add(new AiAttempt(provider, false, skip.Value.Code, skip.Value.Message, 0));
                await WriteAsync(request, correlation, route.Provider, provider, null, AiExecutionMode.Live, false, attemptNumber, failedProvider, 0, null, null, null,
                    skip.Value.Code, skip.Value.Message, ct);
                lastCode = skip.Value.Code;
                lastMessage = skip.Value.Message;
                failedProvider ??= provider;
                continue;
            }

            var maxTries = request.Idempotent ? 1 + policy.MaxRetriesPerProvider : 1;
            for (var tryNo = 0; tryNo < maxTries; tryNo++)
            {
                attemptNumber++;
                var call = await _client.CompleteAsync(
                    new AiCompletionRequest(provider, request.System, request.User, request.MaxTokens, request.ExpectJson, policy.TimeoutSeconds, request.ImageBase64, request.ImageMediaType),
                    ct);

                string? text = call.Text;
                var code = call.ErrorCode;
                var message = call.ErrorMessage;

                // A reply that is not the JSON asked for is a failed attempt, however confident it sounds.
                if (call.Success && request.ExpectJson)
                {
                    if (AiJson.TryExtractObject(call.Text, out var json))
                        text = json;
                    else
                    {
                        code = AiErrorCodes.MalformedResponse;
                        message = "The provider's reply was not valid JSON.";
                        text = null;
                    }
                }

                var ok = call.Success && code is null;
                var price = policy.PriceOf(provider);
                var cost = price.Cost(call.InputTokens, call.OutputTokens);

                attempts.Add(new AiAttempt(provider, ok, code, AiLogScrubber.Scrub(message), call.DurationMs));
                await WriteAsync(request, correlation, route.Provider, provider, call.Model, AiExecutionMode.Live, ok, attemptNumber, failedProvider, call.DurationMs,
                    call.InputTokens, call.OutputTokens, cost, code, message, ct);

                if (ok)
                {
                    return new AiTaskResult(true, text, AiExecutionMode.Live, provider, call.Model, failedProvider is not null, null, null, correlation, attempts,
                        call.InputTokens, call.OutputTokens, cost);
                }

                lastCode = code;
                lastMessage = AiLogScrubber.Scrub(message);

                // Only a temporary failure is worth repeating; a bad key or a malformed reply will not improve by asking again.
                var transient = code is AiErrorCodes.Timeout or AiErrorCodes.RateLimited || (code == AiErrorCodes.ProviderError && call.HttpStatus is >= 500);
                if (!transient || tryNo + 1 >= maxTries)
                    break;

                if (policy.RetryDelayMs > 0)
                    await Task.Delay(policy.RetryDelayMs, ct);
            }

            failedProvider ??= provider;
        }

        _logger.LogWarning("AI task {Task} failed on every provider tried ({Code})", request.Task, lastCode);
        return new AiTaskResult(
            false, null, AiExecutionMode.Live, attempts.LastOrDefault()?.Provider ?? route.Provider, null, attempts.Count > 1, lastCode ?? AiErrorCodes.NoProvider,
            lastMessage ?? "No AI provider could run this task.", correlation, attempts, null, null, null);
    }

    private static (string Code, string Message)? SkipReason(AiTaskRequest request, AiTaskPolicy policy, AiCredentials credentials, string provider)
    {
        if (request.AllowedProviders is not null && !request.AllowedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            return (AiErrorCodes.ProviderNotAllowed, $"{provider} is not permitted for this tenant's data.");

        if (!credentials.IsEnabled(provider))
            return (AiErrorCodes.ProviderUnavailable, $"{provider} is switched off.");

        if (!PlatformAiFeatureSettingsService.HasKey(credentials, provider))
            return (AiErrorCodes.ProviderUnavailable, $"No {provider} key is saved.");

        if (!AiCapabilities.IsDeclared(provider, request.Task))
            return (AiErrorCodes.CapabilityUnsupported, $"{provider} is not set up for {request.Task}.");

        if (policy.MaxCostPerCallUsd > 0)
        {
            var estimatedInput = (request.System.Length + request.User.Length) / 4;
            var estimate = policy.PriceOf(provider).Cost(estimatedInput, request.MaxTokens);
            if (estimate is { } e && e > policy.MaxCostPerCallUsd)
                return (AiErrorCodes.CostLimit, $"Estimated cost {e:0.####} USD is above the limit of {policy.MaxCostPerCallUsd:0.####} USD.");
        }

        return null;
    }

    private AiTaskResult Fail(AiTaskRequest request, AiFeatureChoice route, Guid correlation, List<AiAttempt> attempts, string code, string message)
    {
        _logger.LogInformation("AI task {Task} was not run ({Code})", request.Task, code);
        return new AiTaskResult(false, null, route.UsesThirdParty ? AiExecutionMode.Live : AiExecutionMode.Simulator, route.UsesThirdParty ? route.Provider : AiCapabilities.Simulator,
            null, false, code, message, correlation, attempts, null, null, null);
    }

    private async Task WriteAsync(
        AiTaskRequest request, Guid correlation, string requested, string provider, string? model, AiExecutionMode mode, bool ok, int attempt,
        string? fallbackFrom, int durationMs, int? inputTokens, int? outputTokens, decimal? cost, string? code, string? message, CancellationToken ct)
    {
        try
        {
            await _log.WriteAsync(new AiExecution
            {
                CorrelationId = correlation,
                TenantId = request.TenantId,
                Task = request.Task,
                Feature = request.Feature,
                RequestedProvider = requested,
                Provider = provider,
                Model = model,
                Mode = mode,
                Status = ok ? AiExecutionStatus.Succeeded : AiExecutionStatus.Failed,
                Attempt = attempt,
                FallbackFromProvider = fallbackFrom,
                DurationMs = durationMs,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                EstimatedCostUsd = cost,
                ErrorCode = code,
                ErrorSummary = AiLogScrubber.Scrub(message)
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The record is for operators; losing it must not fail a tenant's task.
            _logger.LogWarning(ex, "Could not record an AI execution for {Task}", request.Task);
        }
    }
}
