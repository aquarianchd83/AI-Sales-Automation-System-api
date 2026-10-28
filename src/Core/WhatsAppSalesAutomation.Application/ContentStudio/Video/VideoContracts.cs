namespace WhatsAppSalesAutomation.Application.ContentStudio.Video;

/// <summary>The platform's keys and limits for making videos (Platform &gt; Configuration &gt; ContentStudio).</summary>
public class RenderOptions
{
    public string ShotstackApiKey { get; set; } = string.Empty;

    /// <summary>"stage" (free sandbox, watermarked) or "v1" (production).</summary>
    public string ShotstackEnvironment { get; set; } = "stage";

    public string GoogleTtsApiKey { get; set; } = string.Empty;

    /// <summary>Most videos being made at once across all tenants.</summary>
    public int MaxConcurrent { get; set; } = 3;

    public bool RendererConfigured => !string.IsNullOrWhiteSpace(ShotstackApiKey);

    public bool VoiceConfigured => !string.IsNullOrWhiteSpace(GoogleTtsApiKey);

    public string Environment => string.Equals(ShotstackEnvironment?.Trim(), "v1", StringComparison.OrdinalIgnoreCase) ? "v1" : "stage";

    public int ConcurrencyLimit => Math.Clamp(MaxConcurrent, 1, 50);
}

public static class RenderLimits
{
    public const long MaxVideoBytes = 300L * 1024 * 1024;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);
    public const int MaxPollFailures = 5;
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(45);
    public static readonly string[] AspectRatios = { "16:9", "9:16", "1:1" };
    public const int MinClips = 2;
    public const int MaxClips = 10;
}

/// <summary>Language names the planner offers -&gt; the voice's language code. Anything else is read in Indian English.</summary>
public static class VoiceLanguages
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en-IN", ["Hindi"] = "hi-IN", ["Hinglish"] = "hi-IN", ["Marathi"] = "mr-IN", ["Gujarati"] = "gu-IN",
        ["Punjabi"] = "pa-IN", ["Bengali"] = "bn-IN", ["Tamil"] = "ta-IN", ["Telugu"] = "te-IN", ["Kannada"] = "kn-IN",
    };

    public static string CodeFor(string? language) => language is not null && Codes.TryGetValue(language.Trim(), out var code) ? code : "en-IN";
}

// ----- the outside world ------------------------------------------------------------------------------------------------------------

/// <param name="Wav">A complete WAV file (16-bit mono).</param>
public sealed record VoiceClip(byte[] Wav, double Seconds);

/// <summary>Speaks narration. Provider-agnostic; today Google Cloud Text-to-Speech.</summary>
public interface IVoiceSynthesizer
{
    bool IsConfigured { get; }

    Task<VoiceClip> SynthesizeAsync(string text, string languageCode, string gender, CancellationToken cancellationToken = default);
}

/// <param name="ImageUrl">A short-lived address the renderer can fetch the picture from.</param>
/// <param name="AudioUrl">A short-lived address for the narration.</param>
/// <param name="CameraMove">ZoomIn, ZoomOut, PanLeft, PanRight or Still.</param>
/// <param name="Mood">Natural, Warm, Cool, Dramatic, Bright or Dark.</param>
/// <param name="Transition">None, Fade or Wipe: how the scene comes in and goes out.</param>
public sealed record RenderScene(
    string ImageUrl, string AudioUrl, double StartSeconds, double LengthSeconds, string? OnScreenText, string CameraMove = "ZoomIn", string Mood = "Natural", string Transition = "None");

/// <param name="VideoUrl">A short-lived address for a finished clip.</param>
public sealed record MergeClip(string VideoUrl, double StartSeconds, double LengthSeconds, string Transition);

/// <summary>Several finished clips joined end to end into one video.</summary>
public sealed record MergePlan(string AspectRatio, IReadOnlyList<MergeClip> Clips);

public sealed record RenderPlan(string AspectRatio, IReadOnlyList<RenderScene> Scenes);

public enum RenderPhase
{
    Working = 0,
    Done = 1,
    Failed = 2
}

/// <param name="Percent">The renderer's own progress, 0-100.</param>
public sealed record RenderStatus(RenderPhase Phase, int Percent, string? Url, string? Error);

/// <summary>Makes the video. Provider-agnostic; today Shotstack, a hosted service, so no rendering happens in this application.</summary>
public interface IVideoRenderer
{
    string Name { get; }

    bool IsConfigured { get; }

    /// <returns>The renderer's id for the job.</returns>
    Task<string> SubmitAsync(RenderPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Joins finished clips into one video. Its progress and result are read with <see cref="GetStatusAsync"/> like any other render.</summary>
    Task<string> SubmitMergeAsync(MergePlan plan, CancellationToken cancellationToken = default);

    Task<RenderStatus> GetStatusAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>Copies the finished video into <paramref name="destination"/>, from the renderer's own host only, and no more than <paramref name="maxBytes"/> of it.</summary>
    Task DownloadAsync(string url, long maxBytes, Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>Short-lived, single-file read addresses so the renderer can fetch a tenant's private picture or narration. Nothing else is ever exposed.</summary>
public interface ITenantFileLinks
{
    string CreateTemporaryReadUrl(Guid tenantId, string storageKey, TimeSpan lifetime);
}

/// <summary>Runs a render in the background: the first step, then repeated checks until the renderer is done.</summary>
public interface IVideoRenderQueue
{
    void EnqueueStart(Guid jobId, Guid tenantId);

    void SchedulePoll(Guid jobId, Guid tenantId, TimeSpan delay);
}

// ----- what the tenant sees ---------------------------------------------------------------------------------------------------------

/// <param name="Mode">"Single" (default): one render of the whole script. "Clips": every scene is made as its own clip, then the clips are joined.</param>
public sealed record StartRenderRequest(string? AspectRatio, string? VoiceGender, string? Mode = null);

public sealed record RenderClipDto(int Order, string Status, double Seconds, string? Error);

public sealed record RenderJobDto(
    Guid Id, Guid VersionId, int VersionNumber, string Status, int Progress, string? Stage, string AspectRatio, string VoiceGender, string Renderer, bool IsSimulated, bool IsAiVoice,
    bool HasVideo, long VideoBytes, double VideoSeconds, string? Error, int FlaggedScenes, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, bool IsOfApprovedScript,
    string Mode = "Single", IReadOnlyList<RenderClipDto>? Clips = null);

public sealed record VideoFile(Stream Content, string ContentType, long Length);

public interface IVideoRenderService
{
    /// <summary>Starts making the video for a script version. Charged first and returned if it fails.</summary>
    Task<RenderJobDto> StartAsync(Guid tenantId, Guid versionId, StartRenderRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RenderJobDto>> ListAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);

    Task<RenderJobDto> GetAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Stops a render that has not finished. The charge is returned.</summary>
    Task<RenderJobDto> CancelAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken = default);

    Task<VideoFile> OpenVideoAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>The background half: called by the queue, in the tenant's scope.</summary>
public interface IVideoRenderProcessor
{
    Task StartAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task PollAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>A failure of the voice or video service that can be told to the tenant as it is: the message is safe to show.</summary>
public sealed class VideoProviderException : Exception
{
    public VideoProviderException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
