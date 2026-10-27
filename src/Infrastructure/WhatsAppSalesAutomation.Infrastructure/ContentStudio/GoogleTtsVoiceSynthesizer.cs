using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Video;

namespace WhatsAppSalesAutomation.Infrastructure.ContentStudio;

/// <summary>
/// Google Cloud Text-to-Speech as the narrator. The platform's key is sent in a header, never in the address, so it cannot land in a request
/// log. The voice is chosen by language and gender only (Google picks its standard voice for them), and the clip comes back as 16-bit mono WAV
/// so its length can be worked out exactly from its size - the video is timed to the voice, not guessed.
/// </summary>
public sealed class GoogleTtsVoiceSynthesizer : IVoiceSynthesizer
{
    public const int SampleRate = 24000;
    private const int WavHeaderBytes = 44;
    private const string Endpoint = "https://texttospeech.googleapis.com/v1/text:synthesize";

    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<RenderOptions> _options;
    private readonly ILogger<GoogleTtsVoiceSynthesizer> _logger;

    public GoogleTtsVoiceSynthesizer(HttpClient http, IOptionsSnapshot<RenderOptions> options, ILogger<GoogleTtsVoiceSynthesizer> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => _options.Value.VoiceConfigured;

    public async Task<VoiceClip> SynthesizeAsync(string text, string languageCode, string gender, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new VideoProviderException("The voice service is not set up on this platform yet.");
        if (string.IsNullOrWhiteSpace(text))
            throw new VideoProviderException("A scene has nothing to say.");

        var body = JsonSerializer.Serialize(new
        {
            input = new { text },
            voice = new { languageCode, ssmlGender = string.Equals(gender, "Male", StringComparison.OrdinalIgnoreCase) ? "MALE" : "FEMALE" },
            audioConfig = new { audioEncoding = "LINEAR16", sampleRateHertz = SampleRate },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _options.Value.GoogleTtsApiKey.Trim());
        using var response = await _http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google text-to-speech answered {Status}.", (int)response.StatusCode);
            throw new VideoProviderException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The voice service rejected the platform's key. Ask the platform administrator to check it.",
                HttpStatusCode.TooManyRequests => "The voice service is limiting requests right now. Try again in a little while.",
                HttpStatusCode.BadRequest => "The voice service could not speak this text in this language.",
                _ => "The voice service could not be reached. Try again in a moment.",
            });
        }

        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var base64 = doc.RootElement.TryGetProperty("audioContent", out var audio) && audio.ValueKind == JsonValueKind.String ? audio.GetString() : null;
            if (string.IsNullOrEmpty(base64))
                throw new VideoProviderException("The voice service returned no audio.");

            var wav = Convert.FromBase64String(base64);
            if (wav.Length <= WavHeaderBytes)
                throw new VideoProviderException("The voice service returned no audio.");
            return new VoiceClip(wav, SecondsOf(wav.Length));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new VideoProviderException("The voice service gave an answer that could not be read.", ex);
        }
    }

    /// <summary>The length of a 16-bit mono WAV of this size at the sample rate asked for.</summary>
    public static double SecondsOf(int wavBytes) => Math.Round((wavBytes - WavHeaderBytes) / (double)(SampleRate * 2), 2);
}
