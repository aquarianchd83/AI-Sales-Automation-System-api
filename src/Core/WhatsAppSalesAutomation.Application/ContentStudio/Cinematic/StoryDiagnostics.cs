using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ProductKnowledge;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

/// <summary>One thing the story studio needs in order to do real work, and whether this host has it.</summary>
public sealed record StoryCheckDto(string Key, string Label, bool Ok, string Detail);

public interface IStoryDiagnosticsService
{
    /// <summary>What is set up and what is missing for live use. It never calls a paid service: it reports what is configured, not whether the provider accepts the key.</summary>
    Task<IReadOnlyList<StoryCheckDto>> RunAsync(CancellationToken cancellationToken = default);
}

public class StoryDiagnosticsService : IStoryDiagnosticsService
{
    private readonly IFfmpegService _ffmpeg;
    private readonly IOptionsSnapshot<CinematicOptions> _options;
    private readonly ITenantDocumentStore _store;

    public StoryDiagnosticsService(IFfmpegService ffmpeg, IOptionsSnapshot<CinematicOptions> options, ITenantDocumentStore store)
    {
        _ffmpeg = ffmpeg;
        _options = options;
        _store = store;
    }

    public async Task<IReadOnlyList<StoryCheckDto>> RunAsync(CancellationToken cancellationToken = default)
    {
        var o = _options.Value;
        var ffmpegOk = await _ffmpeg.IsAvailableAsync(cancellationToken);
        var font = _ffmpeg.FindDevanagariFont();

        return new[]
        {
            new StoryCheckDto("ffmpeg", "ffmpeg (joins clips, burns subtitles)", ffmpegOk,
                ffmpegOk ? "Found." : "Not found. Install ffmpeg or set its path under Platform > Integrations > Story studio; without it no video can be made."),
            new StoryCheckDto("font", "Devanagari subtitle font", font is not null,
                font is not null ? $"Found \"{font}\"." : "None found on this host. Hindi subtitles would show as boxes. Install Noto Sans Devanagari and set the subtitle fonts folder and font name under Platform > Integrations > Story studio."),
            new StoryCheckDto("storage", "File storage (S3)", _store.IsConfigured,
                _store.IsConfigured ? "Configured." : "Not configured. Clips, pictures and finished videos cannot be saved."),
            new StoryCheckDto("veo", "Veo (Gemini API key)", !string.IsNullOrWhiteSpace(o.GeminiApiKey),
                string.IsNullOrWhiteSpace(o.GeminiApiKey) ? "No key. Veo is unavailable; test clips and Flow import still work. Set it under Platform > Integrations > Story studio." : "A key is set. It has not been tried against Google from here."),
            new StoryCheckDto("pictures", "Character pictures", o.CharacterImageProvider != "OpenAI" || !string.IsNullOrWhiteSpace(o.OpenAiApiKey),
                o.CharacterImageProvider != "OpenAI" ? "Using free placeholder pictures." :
                string.IsNullOrWhiteSpace(o.OpenAiApiKey) ? "OpenAI pictures are chosen but there is no OpenAI key. Set it under Platform > Integrations > Story studio." : "An OpenAI key is set. It has not been tried from here."),
            new StoryCheckDto("voice", "Voice-over (OpenAI speech)", o.SpeechProvider != "OpenAI" || !string.IsNullOrWhiteSpace(o.OpenAiApiKey),
                o.SpeechProvider != "OpenAI" ? "Using free test tones. The clips keep their own voices unless you add a voice-over." :
                string.IsNullOrWhiteSpace(o.OpenAiApiKey) ? "OpenAI speech is chosen but there is no OpenAI key. Set it under Platform > Integrations > Story studio." : "An OpenAI key is set. It has not been tried from here.")
        };
    }
}
