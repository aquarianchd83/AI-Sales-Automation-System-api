using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

/// <summary>What a scene needs from whichever provider makes its clip.</summary>
public sealed record SceneClipRequest(
    Guid SceneId,
    string Prompt,
    int TargetSeconds,
    string AspectRatio,
    IReadOnlyList<string> ReferenceImageKeys,
    string IdempotencyKey);

public enum SceneClipState
{
    /// <summary>The provider is still working; poll again with the same ProviderJobId.</summary>
    Running = 0,
    Completed = 1,
    Failed = 2,
    /// <summary>Flow import: nothing happens until the user uploads a clip.</summary>
    AwaitingImport = 3
}

/// <summary>A provider's answer. For <see cref="SceneClipState.Completed"/>, <see cref="ClipPath"/> is a local file the pipeline then stores in S3.</summary>
public sealed record SceneClipResult(
    SceneClipState State,
    string? ProviderJobId,
    string? ClipPath,
    decimal? CostUsd,
    string? Error,
    bool Retryable = false);

/// <summary>
/// One way of getting a scene's clip. Veo (Gemini API) is automatic, Flow is a manual import because Flow has no public API, and the simulator
/// makes a test clip for free. The pipeline never knows which one it is talking to, so a project can switch between them.
/// Submit must be idempotent per <see cref="SceneClipRequest.IdempotencyKey"/>, and Poll must work from the ProviderJobId alone so a restarted
/// worker resumes instead of paying for a second generation.
/// </summary>
public interface ISceneVideoProvider
{
    SceneVideoProviderKind Kind { get; }

    /// <summary>Estimated price for a clip, shown to the user before they confirm. Null when the provider cannot say.</summary>
    decimal? EstimateCostUsd(int seconds);

    Task<SceneClipResult> SubmitAsync(SceneClipRequest request, CancellationToken cancellationToken = default);

    Task<SceneClipResult> PollAsync(string providerJobId, CancellationToken cancellationToken = default);
}

public interface ISceneVideoProviderFactory
{
    ISceneVideoProvider For(SceneVideoProviderKind kind);
}

/// <summary>Settings for the cinematic pipeline. Platform settings with defaults in AppSettingCatalog, never appsettings.json.</summary>
public class CinematicOptions
{
    /// <summary>Full path to ffmpeg. Empty = "ffmpeg" on PATH. Local/dev installs only for now.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    public string FfprobePath { get; set; } = string.Empty;

    public string VeoModel { get; set; } = "veo-3.1-fast-generate-preview";

    /// <summary>Estimate used for the pre-spend confirmation (USD per generated second). Replace with the figure on Google's pricing page.</summary>
    public decimal VeoUsdPerSecond { get; set; } = 0.12m;

    public int MaxAutomaticRetries { get; set; } = 1;

    /// <summary>Folder holding a Devanagari-capable font (for example Noto Sans Devanagari) that ffmpeg's subtitle renderer may use. Empty = the host's own fonts.</summary>
    public string SubtitleFontsDirectory { get; set; } = string.Empty;

    /// <summary>Font family name to use for subtitles. Empty = the renderer's default, which may lack Devanagari on a bare server.</summary>
    public string SubtitleFontName { get; set; } = string.Empty;

    /// <summary>"Simulator" (free test tones) or "OpenAI". Only used when the user adds a voice-over to a scene.</summary>
    public string SpeechProvider { get; set; } = "Simulator";

    public string SpeechModel { get; set; } = "gpt-4o-mini-tts";

    /// <summary>Estimate per spoken line, shown before a paid voice-over. Check OpenAI pricing; not billing data.</summary>
    public decimal SpeechUsdPerLine { get; set; } = 0.02m;

    /// <summary>"Simulator" (free placeholder pictures) or "OpenAI".</summary>
    public string CharacterImageProvider { get; set; } = "Simulator";

    public string ImageModel { get; set; } = "gpt-image-1";

    /// <summary>Estimate shown before a paid picture. Check OpenAI's pricing page; this is not billing data.</summary>
    public decimal ImageUsdEstimate { get; set; } = 0.17m;
}
