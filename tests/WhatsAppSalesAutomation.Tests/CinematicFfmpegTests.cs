using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Infrastructure.Cinematic;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Runs the real ffmpeg. Does nothing on a machine without it (set FFMPEG_PATH / FFPROBE_PATH, or put both on PATH).</summary>
public class CinematicFfmpegTests
{
    private sealed class Snapshot : IOptionsSnapshot<CinematicOptions>
    {
        public CinematicOptions Value { get; } = new()
        {
            FfmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? string.Empty,
            FfprobePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? string.Empty
        };

        public CinematicOptions Get(string? name) => Value;
    }

    [Fact]
    public async Task Test_clips_merge_into_a_vertical_mp4_that_passes_the_final_video_rules()
    {
        var ffmpeg = new FfmpegService(new Snapshot());
        if (!await ffmpeg.IsAvailableAsync()) return;

        var dir = Path.Combine(Path.GetTempPath(), "cine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var clips = new List<string>();
            for (var i = 1; i <= 3; i++)
            {
                var clip = Path.Combine(dir, $"c{i}.mp4");
                await ffmpeg.CreateTestClipAsync(clip, 10, $"scene {i}");
                clips.Add(clip);
            }

            var output = Path.Combine(dir, "final.mp4");
            await ffmpeg.MergeAsync(clips, output, subtitlesPath: null);

            var probe = await ffmpeg.ProbeAsync(output);
            Assert.Equal(1080, probe.Width);
            Assert.Equal(1920, probe.Height);
            Assert.True(probe.HasAudio);
            Assert.InRange(probe.DurationSeconds, 29, 31.5);
            Assert.True(FinalVideoRules.Check(probe, 30).Passed, string.Join("; ", FinalVideoRules.Check(probe, 30).Problems));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
