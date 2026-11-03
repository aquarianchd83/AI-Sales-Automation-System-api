using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

namespace WhatsAppSalesAutomation.Infrastructure.Cinematic;

/// <summary>Free: a tone as long as the line would take to say, so timing and mixing can be tried without paying for speech. It is not speech.</summary>
public class SimulatorSpeechProvider
{
    private readonly IFfmpegService _ffmpeg;

    public SimulatorSpeechProvider(IFfmpegService ffmpeg) => _ffmpeg = ffmpeg;

    public async Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"speech-{Guid.NewGuid():N}.mp3");
        try
        {
            await _ffmpeg.CreateToneAsync(path, Math.Clamp(request.Text.Length * 60, 400, 8000), ct);
            return new SpeechResult(true, path, 0m, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new SpeechResult(false, null, null, "The test tone could not be made: " + ex.Message);
        }
    }
}

/// <summary>
/// OpenAI speech. UNVERIFIED against the live API (request shape follows OpenAI's documented audio/speech endpoint), and Hindi/Hinglish quality is
/// untested. One voice per character, chosen from the character's voice notes; the notes and language also go in as delivery instructions.
/// </summary>
public class OpenAiSpeechProvider
{
    private static readonly string[] Female = { "coral", "nova", "shimmer" };
    private static readonly string[] Male = { "onyx", "echo", "ash" };
    private static readonly string[] FemaleWords = { "female", "woman", "wife", "girl", "mother", "lady", "she", "mahila", "aurat" };

    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<CinematicOptions> _options;
    private readonly IPlatformAiConfigProvider _credentials;

    public OpenAiSpeechProvider(HttpClient http, IOptionsSnapshot<CinematicOptions> options, IPlatformAiConfigProvider credentials)
    {
        _http = http;
        _options = options;
        _credentials = credentials;
    }

    /// <summary>The same character always gets the same voice, so identity holds across scenes.</summary>
    public static string VoiceFor(string characterName, string? hint)
    {
        var female = FemaleWords.Any(w => (hint ?? string.Empty).Contains(w, StringComparison.OrdinalIgnoreCase));
        var pool = female ? Female : Male;
        var stable = characterName.Aggregate(17, (h, c) => unchecked(h * 31 + char.ToLowerInvariant(c)));
        return pool[Math.Abs(stable % pool.Length)];
    }

    public async Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        var key = _credentials.Get() is { } c && c.IsEnabled("OpenAI") ? c.OpenAiApiKey : null;
        if (string.IsNullOrWhiteSpace(key)) return new SpeechResult(false, null, null, "No ChatGPT (OpenAI) key is saved, or it is switched off, under Configuration > AI Providers.");

        var body = new
        {
            model = _options.Value.SpeechModel,
            input = request.Text,
            voice = VoiceFor(request.CharacterName, request.VoiceHint),
            instructions = $"Speak in {request.Language}. {request.VoiceHint}".Trim(),
            response_format = "mp3"
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech") { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await _http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode) return new SpeechResult(false, null, null, $"OpenAI refused the request ({(int)response.StatusCode}).");

            var path = Path.Combine(Path.GetTempPath(), $"speech-{Guid.NewGuid():N}.mp3");
            await using (var file = File.Create(path)) await response.Content.CopyToAsync(file, ct);
            return new SpeechResult(true, path, _options.Value.SpeechUsdPerLine, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SpeechResult(false, null, null, "Lost contact with OpenAI: " + ex.Message);
        }
    }
}

public class SpeechProviderSelector : ISpeechProvider
{
    private readonly IOptionsSnapshot<CinematicOptions> _options;
    private readonly SimulatorSpeechProvider _simulator;
    private readonly OpenAiSpeechProvider _openAi;

    public SpeechProviderSelector(IOptionsSnapshot<CinematicOptions> options, SimulatorSpeechProvider simulator, OpenAiSpeechProvider openAi)
    {
        _options = options;
        _simulator = simulator;
        _openAi = openAi;
    }

    private bool UsesOpenAi => string.Equals(_options.Value.SpeechProvider, "OpenAI", StringComparison.OrdinalIgnoreCase);

    public string Name => UsesOpenAi ? "OpenAI" : "Simulator";

    public decimal? EstimateCostUsd() => UsesOpenAi ? _options.Value.SpeechUsdPerLine : 0m;

    public Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default) =>
        UsesOpenAi ? _openAi.SynthesizeAsync(request, cancellationToken) : _simulator.SynthesizeAsync(request, cancellationToken);
}
