using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
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
    private readonly IPlatformAiConfigProvider _credentials;

    public StoryDiagnosticsService(IFfmpegService ffmpeg, IOptionsSnapshot<CinematicOptions> options, ITenantDocumentStore store, IPlatformAiConfigProvider credentials)
    {
        _ffmpeg = ffmpeg;
        _options = options;
        _store = store;
        _credentials = credentials;
    }

    public async Task<IReadOnlyList<StoryCheckDto>> RunAsync(CancellationToken cancellationToken = default)
    {
        var o = _options.Value;
        var ai = _credentials.Get();
        var hasGoogle = ai.IsEnabled("Google") && !string.IsNullOrWhiteSpace(ai.GoogleApiKey);
        var hasOpenAi = ai.IsEnabled("OpenAI") && !string.IsNullOrWhiteSpace(ai.OpenAiApiKey);
        var veoOn = string.Equals(o.VideoProvider, "Google", StringComparison.OrdinalIgnoreCase);
        const string where = "Add it (and switch the provider on) under Configuration > AI Providers.";
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
            new StoryCheckDto("veo", "Veo (Gemini key)", !veoOn || hasGoogle,
                !veoOn ? "Gemini (Veo) is switched off: clips come from the free test clips or Google Flow. Switch it on under Platform > Integrations > Story Studio." :
                !hasGoogle ? "Gemini (Veo) is switched on but no Gemini key is available. " + where : "The Gemini key from AI Providers is used. It has not been tried against Google from here."),
            new StoryCheckDto("pictures", "Character pictures", o.CharacterImageProvider != "OpenAI" || hasOpenAi,
                o.CharacterImageProvider != "OpenAI" ? "Using free placeholder pictures." :
                !hasOpenAi ? "OpenAI pictures are chosen but there is no ChatGPT key. " + where : "The ChatGPT key from AI Providers is used. It has not been tried from here."),
            new StoryCheckDto("voice", "Voice-over (OpenAI speech)", o.SpeechProvider != "OpenAI" || hasOpenAi,
                o.SpeechProvider != "OpenAI" ? "Using free test tones. The clips keep their own voices unless you add a voice-over." :
                !hasOpenAi ? "OpenAI speech is chosen but there is no ChatGPT key. " + where : "The ChatGPT key from AI Providers is used. It has not been tried from here.")
        };
    }
}
