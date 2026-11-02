using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Cinematic;

/// <summary>Free, local: makes a test clip with ffmpeg so the whole pipeline can run without a key or a bill.</summary>
public class SimulatorSceneVideoProvider : ISceneVideoProvider
{
    private readonly IFfmpegService _ffmpeg;

    public SimulatorSceneVideoProvider(IFfmpegService ffmpeg) => _ffmpeg = ffmpeg;

    public SceneVideoProviderKind Kind => SceneVideoProviderKind.Simulator;

    // The idempotency key contains colons, which a Windows file name cannot.
    private static string TempPath(string key) => Path.Combine(Path.GetTempPath(), $"sim-{key.Replace(':', '-')}.mp4");

    public decimal? EstimateCostUsd(int seconds) => 0m;

    public async Task<SceneClipResult> SubmitAsync(SceneClipRequest request, CancellationToken cancellationToken = default)
    {
        var path = TempPath(request.IdempotencyKey);
        try
        {
            if (!File.Exists(path)) await _ffmpeg.CreateTestClipAsync(path, Math.Max(1, request.TargetSeconds), "Test data", cancellationToken);
            return new(SceneClipState.Completed, request.IdempotencyKey, path, 0m, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(SceneClipState.Failed, null, null, null, $"The test clip could not be made: {ex.Message}");
        }
    }

    public Task<SceneClipResult> PollAsync(string providerJobId, CancellationToken cancellationToken = default)
    {
        var path = TempPath(providerJobId);
        return Task.FromResult(File.Exists(path)
            ? new SceneClipResult(SceneClipState.Completed, providerJobId, path, 0m, null)
            : new SceneClipResult(SceneClipState.Failed, providerJobId, null, null, "The test clip is gone."));
    }
}

/// <summary>
/// Google Flow has no public API, so this is the assisted path: the pipeline prepares the prompt, the user generates the clip in Flow and uploads it.
/// Submit therefore never produces a clip; it parks the scene as AwaitingImport. The upload endpoint completes it.
/// </summary>
public class FlowImportSceneVideoProvider : ISceneVideoProvider
{
    public SceneVideoProviderKind Kind => SceneVideoProviderKind.FlowImport;

    public decimal? EstimateCostUsd(int seconds) => null;

    public Task<SceneClipResult> SubmitAsync(SceneClipRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new SceneClipResult(SceneClipState.AwaitingImport, request.IdempotencyKey, null, null, null));

    public Task<SceneClipResult> PollAsync(string providerJobId, CancellationToken cancellationToken = default)
        => Task.FromResult(new SceneClipResult(SceneClipState.AwaitingImport, providerJobId, null, null, null));
}

/// <summary>
/// Veo through the Gemini API (long-running operation: submit, then poll). UNVERIFIED against the live API: the request and response shapes
/// follow Google's documented REST pattern and must be proven with a real key before this is trusted. Never auto-retries a submit, because a
/// lost response may still have been billed; the caller decides.
/// </summary>
public class VeoSceneVideoProvider : ISceneVideoProvider
{
    private const string Base = "https://generativelanguage.googleapis.com/v1beta/";

    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<CinematicOptions> _options;

    public VeoSceneVideoProvider(HttpClient http, IOptionsSnapshot<CinematicOptions> options)
    {
        _http = http;
        _options = options;
    }

    public SceneVideoProviderKind Kind => SceneVideoProviderKind.Veo;

    public decimal? EstimateCostUsd(int seconds) => _options.Value.VeoUsdPerSecond * seconds;

    public async Task<SceneClipResult> SubmitAsync(SceneClipRequest request, CancellationToken cancellationToken = default)
    {
        var key = _options.Value.GeminiApiKey;
        if (string.IsNullOrWhiteSpace(key)) return new(SceneClipState.Failed, null, null, null, "No Gemini API key is configured.");

        var body = new
        {
            instances = new[] { new { prompt = request.Prompt } },
            parameters = new { aspectRatio = request.AspectRatio }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Base}models/{_options.Value.VeoModel}:predictLongRunning") { Content = JsonContent.Create(body) };
        message.Headers.Add("x-goog-api-key", key);

        try
        {
            using var response = await _http.SendAsync(message, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(SceneClipState.Failed, null, null, null, $"Veo refused the request ({(int)response.StatusCode}): {Trim(text)}", Retryable: (int)response.StatusCode is 429 or >= 500);

            using var doc = JsonDocument.Parse(text);
            var name = doc.RootElement.GetProperty("name").GetString();
            return new(SceneClipState.Running, name, null, EstimateCostUsd(request.TargetSeconds), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // Contact may have been lost AFTER Google accepted the job, so this is not retryable by default.
            return new(SceneClipState.Failed, null, null, null, $"Lost contact with Veo: {ex.Message}");
        }
    }

    public async Task<SceneClipResult> PollAsync(string providerJobId, CancellationToken cancellationToken = default)
    {
        var key = _options.Value.GeminiApiKey;
        using var message = new HttpRequestMessage(HttpMethod.Get, Base + providerJobId);
        message.Headers.Add("x-goog-api-key", key);
        try
        {
            using var response = await _http.SendAsync(message, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(SceneClipState.Failed, providerJobId, null, null, $"Polling failed ({(int)response.StatusCode}): {Trim(text)}", Retryable: (int)response.StatusCode is 429 or >= 500);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("done", out var done) || !done.GetBoolean()) return new(SceneClipState.Running, providerJobId, null, null, null);
            if (root.TryGetProperty("error", out var err)) return new(SceneClipState.Failed, providerJobId, null, null, Trim(err.ToString()));

            var uri = root.GetProperty("response").GetProperty("generateVideoResponse").GetProperty("generatedSamples")[0].GetProperty("video").GetProperty("uri").GetString();
            var path = Path.Combine(Path.GetTempPath(), $"veo-{Guid.NewGuid():N}.mp4");
            using var download = new HttpRequestMessage(HttpMethod.Get, uri);
            download.Headers.Add("x-goog-api-key", key);
            using var file = await _http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            file.EnsureSuccessStatusCode();
            await using (var target = File.Create(path)) await file.Content.CopyToAsync(target, cancellationToken);
            return new(SceneClipState.Completed, providerJobId, path, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Polling is safe to repeat (no new charge), so a lost poll is simply "still running".
            return new(SceneClipState.Running, providerJobId, null, null, $"Poll failed, will retry: {ex.Message}");
        }
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400];
}

public class SceneVideoProviderFactory : ISceneVideoProviderFactory
{
    private readonly IReadOnlyDictionary<SceneVideoProviderKind, ISceneVideoProvider> _providers;

    public SceneVideoProviderFactory(IEnumerable<ISceneVideoProvider> providers) => _providers = providers.ToDictionary(p => p.Kind);

    public ISceneVideoProvider For(SceneVideoProviderKind kind) =>
        _providers.TryGetValue(kind, out var provider) ? provider : throw new InvalidOperationException($"No video provider is registered for {kind}.");
}
