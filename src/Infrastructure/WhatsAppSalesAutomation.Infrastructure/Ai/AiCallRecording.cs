using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Records the calls the application's existing AI features make (customer chat, knowledge base embeddings, suggestions, the description
/// rewrite, lead discovery) in the same AI call log as the product-knowledge tasks, so each provider's capability for them can be verified by
/// real use and its cost seen. The features themselves are untouched: the wrappers below only time the call and write one row afterwards. No
/// prompt, reply, document or key is stored, a simulated call is not recorded, and a failure to record never fails the feature.
/// </summary>
public sealed class AiCallRecorder
{
    /// <summary>Embeddings run once per chunk, so a busy knowledge base would flood the log: those record at most one success per provider every
    /// ten minutes and one failure a minute - enough to prove the capability and to show a problem.</summary>
    private static readonly TimeSpan ThrottledSuccessWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ThrottledFailureWindow = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, DateTime> LastRecorded = new();

    private readonly IAiExecutionLog _log;
    private readonly IAiTaskPolicyProvider _policy;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AiCallRecorder> _logger;

    public AiCallRecorder(IAiExecutionLog log, IAiTaskPolicyProvider policy, ITenantContext tenant, ILogger<AiCallRecorder> logger)
    {
        _log = log;
        _policy = policy;
        _tenant = tenant;
        _logger = logger;
    }

    public async Task RecordAsync(
        string task, string? feature, string provider, string? model, bool success, int durationMs, int? inputTokens = null, int? outputTokens = null,
        string? errorSummary = null, bool throttle = false)
    {
        try
        {
            if (throttle && !Due(provider, task, success))
                return;

            await _log.WriteAsync(new AiExecution
            {
                CorrelationId = Guid.NewGuid(),
                TenantId = _tenant.TenantId,
                Task = task,
                Feature = feature,
                RequestedProvider = provider,
                Provider = provider,
                Model = model,
                Mode = AiExecutionMode.Live,
                Status = success ? AiExecutionStatus.Succeeded : AiExecutionStatus.Failed,
                DurationMs = durationMs,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                EstimatedCostUsd = _policy.Get().PriceOf(provider).Cost(inputTokens, outputTokens),
                ErrorCode = success ? null : AiErrorCodes.ProviderError,
                ErrorSummary = success ? null : AiLogScrubber.Scrub(errorSummary ?? "The provider call failed.")
            });
        }
        catch (Exception ex)
        {
            // The record is for operators; losing it must never fail a customer reply or a tenant's screen.
            _logger.LogWarning(ex, "Could not record an AI call for {Task}", task);
        }
    }

    /// <summary>Forgets when throttled calls were last recorded (tests).</summary>
    public static void ClearThrottle() => LastRecorded.Clear();

    private static bool Due(string provider, string task, bool success)
    {
        var key = $"{provider}|{task}|{success}";
        var now = DateTime.UtcNow;
        var window = success ? ThrottledSuccessWindow : ThrottledFailureWindow;
        if (LastRecorded.TryGetValue(key, out var last) && now - last < window)
            return false;
        LastRecorded[key] = now;
        return true;
    }

    /// <summary>The provider a credentials-based client really calls for <paramref name="choice"/>, or null when it falls back to the simulator.</summary>
    public static string? LiveProvider(AiCredentials? credentials, string? choice)
    {
        if (credentials is null)
            return null;
        var provider = PlatformAiFeatureSettingsService.Canonical(choice);
        return provider is not null && PlatformAiFeatureSettingsService.HasKey(credentials, provider) ? provider : null;
    }

    public static string ChatModel(AiCredentials c, string provider) => provider switch
    {
        "OpenAI" => c.OpenAiChatModel,
        "Google" => c.GoogleChatModel,
        _ => c.AnthropicModel
    };

    public static string EmbeddingModel(AiCredentials c, string provider) => provider == "Google" ? c.GoogleEmbeddingModel : c.OpenAiEmbeddingModel;
}

/// <summary>Customer chat replies: records each live reply with the model, tokens and time the client reports.</summary>
public sealed class RecordingAiService : IAiService
{
    private readonly AiServiceFactory _inner;
    private readonly IPlatformAiConfigProvider _credentials;
    private readonly AiCallRecorder _recorder;

    public RecordingAiService(AiServiceFactory inner, IPlatformAiConfigProvider credentials, AiCallRecorder recorder)
    {
        _inner = inner;
        _credentials = credentials;
        _recorder = recorder;
    }

    public async Task<AiReplyResult> GetResponseAsync(AiConversationContext context, CancellationToken cancellationToken = default)
    {
        var credentials = _credentials.Get();
        var provider = AiCallRecorder.LiveProvider(credentials, credentials?.Provider);
        if (provider is null)
            return await _inner.GetResponseAsync(context, cancellationToken);

        var watch = Stopwatch.StartNew();
        try
        {
            var result = await _inner.GetResponseAsync(context, cancellationToken);
            await _recorder.RecordAsync(AiTaskKinds.ConversationReply, AiFeatures.Conversation, provider, result.ModelUsed, !result.ProviderFailed,
                result.LatencyMs > 0 ? result.LatencyMs : (int)watch.ElapsedMilliseconds, result.PromptTokens, result.CompletionTokens,
                result.ProviderFailed ? "The provider call failed; the customer got the fallback reply." : null);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _recorder.RecordAsync(AiTaskKinds.ConversationReply, AiFeatures.Conversation, provider, AiCallRecorder.ChatModel(credentials!, provider), false,
                (int)watch.ElapsedMilliseconds, errorSummary: ex.Message);
            throw;
        }
    }
}

/// <summary>One-shot text (keyword suggestions, the description rewrite): recorded under the use the caller named with <see cref="AiCallLabel"/>.</summary>
public sealed class RecordingAiTextGenerator : IAiTextGenerator
{
    private readonly AiTextGenerator _inner;
    private readonly IPlatformAiConfigProvider _credentials;
    private readonly AiCallRecorder _recorder;

    public RecordingAiTextGenerator(AiTextGenerator inner, IPlatformAiConfigProvider credentials, AiCallRecorder recorder)
    {
        _inner = inner;
        _credentials = credentials;
        _recorder = recorder;
    }

    public async Task<string?> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default, string? provider = null)
    {
        var task = AiCallLabel.Current;
        var credentials = _credentials.Get();
        var live = task is null ? null : AiCallRecorder.LiveProvider(credentials, string.IsNullOrWhiteSpace(provider) ? credentials?.Provider : provider);

        var watch = Stopwatch.StartNew();
        var reply = await _inner.GenerateAsync(systemPrompt, userPrompt, maxTokens, cancellationToken, provider);

        // The generator answers null both for "nothing to ask" and for a failed call; with a live provider and a key, null means it failed.
        if (live is not null)
        {
            await _recorder.RecordAsync(task!, null, live, AiCallRecorder.ChatModel(credentials!, live), !string.IsNullOrWhiteSpace(reply), (int)watch.ElapsedMilliseconds,
                errorSummary: "The provider did not answer.");
        }

        return reply;
    }
}

/// <summary>Knowledge base embeddings with the active (tenant) embedder.</summary>
public sealed class RecordingEmbeddingService : IEmbeddingService
{
    private readonly TenantEmbeddingService _inner;
    private readonly IPlatformAiConfigProvider _credentials;
    private readonly AiCallRecorder _recorder;

    public RecordingEmbeddingService(TenantEmbeddingService inner, IPlatformAiConfigProvider credentials, AiCallRecorder recorder)
    {
        _inner = inner;
        _credentials = credentials;
        _recorder = recorder;
    }

    public string ProviderName => _inner.ProviderName;

    public string ModelName => _inner.ModelName;

    public bool IsAvailable => _inner.IsAvailable;

    public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var credentials = _credentials.Get();
        return EmbeddingRecording.RunAsync(_recorder, AiCallRecorder.LiveProvider(credentials, credentials?.EmbeddingProvider), credentials,
            () => _inner.GetEmbeddingAsync(text, cancellationToken));
    }
}

/// <summary>The platform's own embedder (platform knowledge articles, and tenant questions matched against them).</summary>
public sealed class RecordingPlatformEmbeddingService : IPlatformEmbeddingService
{
    private readonly PlatformEmbeddingService _inner;
    private readonly IPlatformAiConfigProvider _credentials;
    private readonly AiCallRecorder _recorder;

    public RecordingPlatformEmbeddingService(PlatformEmbeddingService inner, IPlatformAiConfigProvider credentials, AiCallRecorder recorder)
    {
        _inner = inner;
        _credentials = credentials;
        _recorder = recorder;
    }

    public string ProviderName => _inner.ProviderName;

    public string ModelName => _inner.ModelName;

    public bool IsAvailable => _inner.IsAvailable;

    public bool IsSimulated => _inner.IsSimulated;

    public bool RealProviderRequestedButUnconfigured => _inner.RealProviderRequestedButUnconfigured;

    public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var provider = _inner.IsSimulated ? null : PlatformAiFeatureSettingsService.Canonical(_inner.ProviderName);
        return EmbeddingRecording.RunAsync(_recorder, provider, _credentials.Get(), () => _inner.GetEmbeddingAsync(text, cancellationToken));
    }
}

internal static class EmbeddingRecording
{
    public static async Task<float[]> RunAsync(AiCallRecorder recorder, string? provider, AiCredentials? credentials, Func<Task<float[]>> call)
    {
        if (provider is null)
            return await call();

        var model = credentials is null ? null : AiCallRecorder.EmbeddingModel(credentials, provider);
        var watch = Stopwatch.StartNew();
        try
        {
            var vector = await call();
            // The embedding clients answer a failed call with an empty vector instead of throwing, so the search degrades to "nothing found".
            var ok = vector.Length > 0;
            await recorder.RecordAsync(AiTaskKinds.KnowledgeEmbedding, AiFeatures.Conversation, provider, model, ok, (int)watch.ElapsedMilliseconds,
                errorSummary: ok ? null : "The provider returned no embedding.", throttle: true);
            return vector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await recorder.RecordAsync(AiTaskKinds.KnowledgeEmbedding, AiFeatures.Conversation, provider, model, false, (int)watch.ElapsedMilliseconds,
                errorSummary: ex.Message, throttle: true);
            throw;
        }
    }
}

/// <summary>Lead discovery's web-search agent (Claude): one record per run, with its token use.</summary>
public sealed class RecordingLeadDiscoveryAgent : ILeadDiscoveryAgent
{
    private readonly AnthropicLeadDiscoveryAgent _inner;
    private readonly AiCallRecorder _recorder;

    public RecordingLeadDiscoveryAgent(AnthropicLeadDiscoveryAgent inner, AiCallRecorder recorder)
    {
        _inner = inner;
        _recorder = recorder;
    }

    public async Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await _inner.DiscoverAsync(request, cancellationToken);
            if (result.IsConfigured)
            {
                await _recorder.RecordAsync(AiTaskKinds.LeadDiscovery, AiFeatures.DiscoveryLead, "Anthropic", result.Model, true, (int)watch.ElapsedMilliseconds,
                    result.Usage.InputTokens + result.Usage.CacheReadInputTokens + result.Usage.CacheCreationInputTokens, result.Usage.OutputTokens);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _recorder.RecordAsync(AiTaskKinds.LeadDiscovery, AiFeatures.DiscoveryLead, "Anthropic", null, false, (int)watch.ElapsedMilliseconds, errorSummary: ex.Message);
            throw;
        }
    }
}
