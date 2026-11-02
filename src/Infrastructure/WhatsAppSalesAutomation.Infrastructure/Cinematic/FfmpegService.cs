using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

namespace WhatsAppSalesAutomation.Infrastructure.Cinematic;

public class FfmpegService : IFfmpegService
{
    private readonly IOptionsSnapshot<CinematicOptions> _options;

    public FfmpegService(IOptionsSnapshot<CinematicOptions> options) => _options = options;

    private string Ffmpeg => string.IsNullOrWhiteSpace(_options.Value.FfmpegPath) ? "ffmpeg" : _options.Value.FfmpegPath;

    private string Ffprobe => string.IsNullOrWhiteSpace(_options.Value.FfprobePath) ? "ffprobe" : _options.Value.FfprobePath;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (code, _, _) = await RunAsync(Ffmpeg, new[] { "-version" }, cancellationToken);
            return code == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    public async Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var (code, output, error) = await RunAsync(Ffprobe,
            new[] { "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path }, cancellationToken);
        if (code != 0) throw new InvalidOperationException($"ffprobe failed: {Tail(error)}");

        using var doc = JsonDocument.Parse(output);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("The file has no video stream.");

        var hasAudio = streams.Any(s => s.GetProperty("codec_type").GetString() == "audio");
        var format = doc.RootElement.GetProperty("format");
        var duration = double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return new VideoProbe(
            video.GetProperty("width").GetInt32(),
            video.GetProperty("height").GetInt32(),
            ParseFrameRate(video.GetProperty("avg_frame_rate").GetString()),
            duration,
            hasAudio,
            format.GetProperty("format_name").GetString() ?? string.Empty);
    }

    public async Task CreateTestClipAsync(string outputPath, int seconds, string label, CancellationToken cancellationToken = default)
    {
        // Colour + tone only: drawtext needs a font file that differs per machine, and the label is metadata, not a requirement.
        var args = new List<string>
        {
            "-y", "-f", "lavfi", "-i", $"color=c=0x203040:s=720x1280:r=30:d={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
            "-metadata", $"title={label}", outputPath
        };
        await RunCheckedAsync(Ffmpeg, args, cancellationToken);
    }

    public async Task MergeAsync(IReadOnlyList<string> clipPaths, string outputPath, string? subtitlesPath, CancellationToken cancellationToken = default)
    {
        if (clipPaths.Count == 0) throw new ArgumentException("There are no clips to merge.", nameof(clipPaths));

        var args = new List<string> { "-y" };
        var filter = new StringBuilder();
        for (var i = 0; i < clipPaths.Count; i++)
        {
            args.Add("-i");
            args.Add(clipPaths[i]);
            var probe = await ProbeAsync(clipPaths[i], cancellationToken);
            filter.Append(CultureInfo.InvariantCulture,
                $"[{i}:v]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30,format=yuv420p[v{i}];");
            if (probe.HasAudio)
                filter.Append(CultureInfo.InvariantCulture, $"[{i}:a]aresample=44100,aformat=channel_layouts=stereo[a{i}];");
            else
                filter.Append(CultureInfo.InvariantCulture,
                    $"anullsrc=r=44100:cl=stereo,atrim=duration={probe.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}[a{i}];");
        }

        for (var i = 0; i < clipPaths.Count; i++) filter.Append(CultureInfo.InvariantCulture, $"[v{i}][a{i}]");
        filter.Append(CultureInfo.InvariantCulture, $"concat=n={clipPaths.Count}:v=1:a=1[v][a]");

        var videoLabel = "[v]";
        if (!string.IsNullOrWhiteSpace(subtitlesPath))
        {
            // The subtitles filter wants forward slashes and an escaped drive colon.
            var escaped = subtitlesPath.Replace('\\', '/').Replace(":", "\\:");
            filter.Append(CultureInfo.InvariantCulture, $";[v]subtitles='{escaped}'[vs]");
            videoLabel = "[vs]";
        }

        args.AddRange(new[]
        {
            "-filter_complex", filter.ToString(), "-map", videoLabel, "-map", "[a]",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", outputPath
        });
        await RunCheckedAsync(Ffmpeg, args, cancellationToken);
    }

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
            return n / d;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : 0;
    }

    private static async Task RunCheckedAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var (code, _, error) = await RunAsync(exe, args, ct);
        if (code != 0) throw new InvalidOperationException($"ffmpeg failed (exit {code}): {Tail(error)}");
    }

    private static string Tail(string text) => text.Length <= 600 ? text : text[^600..];

    private static async Task<(int Code, string Output, string Error)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList quotes each argument itself, so a path with spaces or a filter with quotes cannot split into extra arguments.
        foreach (var a in args) info.ArgumentList.Add(a);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }
}
