namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

/// <summary>What ffprobe says about a file.</summary>
public sealed record VideoProbe(int Width, int Height, double FramesPerSecond, double DurationSeconds, bool HasAudio, string Container);

public sealed record VideoRuleResult(bool Passed, IReadOnlyList<string> Problems);

/// <summary>The rules a finished short must meet before it can be reviewed or published.</summary>
public static class FinalVideoRules
{
    public static VideoRuleResult Check(VideoProbe probe, int targetSeconds)
    {
        var problems = new List<string>();
        if (probe.Width * 16 != probe.Height * 9) problems.Add($"Not 9:16 ({probe.Width}x{probe.Height}).");
        if (probe.Width < 720) problems.Add($"Too small ({probe.Width}x{probe.Height}); 720x1280 is the minimum.");
        if (probe.FramesPerSecond is < 24 or > 60) problems.Add($"Frame rate {probe.FramesPerSecond:0.##} is outside 24-60.");
        if (!probe.HasAudio) problems.Add("There is no audio stream.");
        var slack = Math.Max(3, targetSeconds * 0.2);
        if (Math.Abs(probe.DurationSeconds - targetSeconds) > slack)
            problems.Add($"Lasts {probe.DurationSeconds:0.#} s; the target is {targetSeconds} s.");
        if (!probe.Container.Contains("mp4", StringComparison.OrdinalIgnoreCase)) problems.Add($"Container is '{probe.Container}', not MP4.");
        return new(problems.Count == 0, problems);
    }
}

/// <summary>Runs the local ffmpeg/ffprobe. Local/dev only for now; the hosting decision is open.</summary>
public interface IFfmpegService
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>A 9:16 test clip with a tone, standing in for a provider clip in simulator mode.</summary>
    Task CreateTestClipAsync(string outputPath, int seconds, string label, CancellationToken cancellationToken = default);

    /// <summary>Joins the clips in order into one 1080x1920 H.264/AAC MP4, normalising size, frame rate and audio first so clips from different
    /// providers concatenate cleanly. A clip with no audio gets silence.</summary>
    Task MergeAsync(IReadOnlyList<string> clipPaths, string outputPath, string? subtitlesPath, CancellationToken cancellationToken = default);
}
