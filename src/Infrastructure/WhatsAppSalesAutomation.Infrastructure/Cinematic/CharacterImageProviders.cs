using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

namespace WhatsAppSalesAutomation.Infrastructure.Cinematic;

/// <summary>Free: a plain labelled placeholder picture so the approve-and-reuse flow can be tried without paying for images.</summary>
public class SimulatorCharacterImageProvider
{
    private readonly IFfmpegService _ffmpeg;

    public SimulatorCharacterImageProvider(IFfmpegService ffmpeg) => _ffmpeg = ffmpeg;

    public async Task<CharacterImageResult> GenerateAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"charimg-{Guid.NewGuid():N}.png");
        try
        {
            await _ffmpeg.CreateTestImageAsync(path, 1024, 1536, ct);
            return new CharacterImageResult(true, await File.ReadAllBytesAsync(path, ct), 0m, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CharacterImageResult(false, null, null, "The test picture could not be made: " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* harmless */ }
        }
    }
}

/// <summary>
/// OpenAI image generation. UNVERIFIED against the live API: the request follows OpenAI's documented images endpoint and must be proven with a real key.
/// A lost response is not retried, because the image may already have been billed.
/// </summary>
public class OpenAiCharacterImageProvider
{
    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<CinematicOptions> _options;

    public OpenAiCharacterImageProvider(HttpClient http, IOptionsSnapshot<CinematicOptions> options)
    {
        _http = http;
        _options = options;
    }

    public async Task<CharacterImageResult> GenerateAsync(string prompt, CancellationToken ct)
    {
        var key = _options.Value.OpenAiApiKey;
        if (string.IsNullOrWhiteSpace(key)) return new CharacterImageResult(false, null, null, "No OpenAI API key is configured for pictures.");

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
        {
            Content = JsonContent.Create(new { model = _options.Value.ImageModel, prompt, size = "1024x1536", n = 1 })
        };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await _http.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new CharacterImageResult(false, null, null, $"OpenAI refused the request ({(int)response.StatusCode}).");

            using var doc = JsonDocument.Parse(text);
            var data = doc.RootElement.GetProperty("data")[0];
            if (!data.TryGetProperty("b64_json", out var b64)) return new CharacterImageResult(false, null, null, "OpenAI returned no image data.");
            return new CharacterImageResult(true, Convert.FromBase64String(b64.GetString()!), _options.Value.ImageUsdEstimate, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or FormatException)
        {
            return new CharacterImageResult(false, null, null, "Lost contact with OpenAI: " + ex.Message);
        }
    }
}

/// <summary>Picks the platform's chosen picture source on every call, so switching the setting needs no restart.</summary>
public class CharacterImageProviderSelector : ICharacterImageProvider
{
    private readonly IOptionsSnapshot<CinematicOptions> _options;
    private readonly SimulatorCharacterImageProvider _simulator;
    private readonly OpenAiCharacterImageProvider _openAi;

    public CharacterImageProviderSelector(IOptionsSnapshot<CinematicOptions> options, SimulatorCharacterImageProvider simulator, OpenAiCharacterImageProvider openAi)
    {
        _options = options;
        _simulator = simulator;
        _openAi = openAi;
    }

    private bool UsesOpenAi => string.Equals(_options.Value.CharacterImageProvider, "OpenAI", StringComparison.OrdinalIgnoreCase);

    public string Name => UsesOpenAi ? "OpenAI" : "Simulator";

    public decimal? EstimateCostUsd() => UsesOpenAi ? _options.Value.ImageUsdEstimate : 0m;

    public Task<CharacterImageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) =>
        UsesOpenAi ? _openAi.GenerateAsync(prompt, cancellationToken) : _simulator.GenerateAsync(cancellationToken);
}
