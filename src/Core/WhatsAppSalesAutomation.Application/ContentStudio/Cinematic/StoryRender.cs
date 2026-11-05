using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

public sealed record StoryRenderDto(
    Guid Id, StoryJobStatus Status, string? Error, IReadOnlyList<string> Problems, double? DurationSeconds, bool HasVideo, DateTime CreatedAt);

/// <summary>Builds the subtitle file from the approved dialogue. Pure, so it is tested without ffmpeg.</summary>
public static class SubtitleBuilder
{
    public sealed record Cue(int StartMs, int EndMs, string Text);

    /// <summary>One cue per dialogue line, shifted by the length of the scenes before it. The scene's real clip length is used, not its target, so the
    /// words stay on the right frames when a provider returned 7.6 seconds instead of 8.</summary>
    public static IReadOnlyList<Cue> Cues(IEnumerable<(int OffsetMs, int SceneMs, IReadOnlyList<StoryDialogueLine> Lines)> scenes)
    {
        var cues = new List<Cue>();
        foreach (var (offset, sceneMs, lines) in scenes)
        {
            foreach (var line in lines.OrderBy(l => l.Order))
            {
                var start = offset + Math.Min(line.StartMs, Math.Max(0, sceneMs - 200));
                var end = offset + Math.Min(line.EndMs, sceneMs);
                if (end <= start) end = start + 300;
                cues.Add(new Cue(start, end, line.Text.Trim()));
            }
        }

        return cues;
    }

    public static string Srt(IReadOnlyList<Cue> cues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            sb.Append(i + 1).Append('\n').Append(Stamp(cues[i].StartMs)).Append(" --> ").Append(Stamp(cues[i].EndMs)).Append('\n')
              .Append(cues[i].Text).Append("\n\n");
        }

        return sb.ToString();
    }

    private static string Stamp(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }
}

public interface IStoryRenderQueue
{
    void Enqueue(Guid renderId, Guid tenantId);
}

public interface IStoryRenderService
{
    Task<StoryRenderDto> StartRenderAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default);

    Task<IReadOnlyList<StoryRenderDto>> ListRendersAsync(Guid tenantId, Guid projectId, CancellationToken ct = default);

    /// <summary>The finished video's bytes for download, or null if the render has none. Tenant-checked.</summary>
    Task<Stream?> OpenFinalAsync(Guid tenantId, Guid renderId, CancellationToken ct = default);

    /// <summary>A scene's clip for preview. Tenant-checked.</summary>
    Task<Stream?> OpenClipAsync(Guid tenantId, Guid sceneId, CancellationToken ct = default);
}

public interface IStoryRenderRunner
{
    Task RunAsync(Guid renderId, CancellationToken ct = default);
}

/// <summary>
/// Joins the scene clips into the final 9:16 MP4 with the dialogue burned in as subtitles, checks it against <see cref="FinalVideoRules"/> and stores it.
/// The clips keep their own audio: the voice comes from the video source. Nothing here makes or pays for speech.
/// </summary>
public class StoryRenderService : IStoryRenderService, IStoryRenderRunner
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly ITenantDocumentStore _store;
    private readonly IFfmpegService _ffmpeg;
    private readonly IStoryRenderQueue _queue;
    private readonly ILogger<StoryRenderService> _logger;

    public StoryRenderService(
        IApplicationDbContext context, ITenantConfigOverrideProvider config, ITenantDocumentStore store, IFfmpegService ffmpeg, IStoryRenderQueue queue,
        ILogger<StoryRenderService> logger)
    {
        _context = context;
        _config = config;
        _store = store;
        _ffmpeg = ffmpeg;
        _queue = queue;
        _logger = logger;
    }

    public async Task<StoryRenderDto> StartRenderAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        if (project.Status != StoryProjectStatus.InReview) throw new ConflictException("Every scene needs a finished clip before the video can be put together.");
        if (!_store.IsConfigured) throw new StorageUnavailableException("File storage is not set up yet.");

        var running = await _context.StoryRenders.IgnoreQueryFilters()
            .AnyAsync(r => r.ProjectId == projectId && (r.Status == StoryJobStatus.Queued || r.Status == StoryJobStatus.Processing), ct);
        if (running) throw new ConflictException("The video is already being put together.");

        var version = await _context.StoryScriptVersions.IgnoreQueryFilters().FirstAsync(v => v.Id == project.CurrentScriptVersionId, ct);
        var render = new StoryRender { TenantId = tenantId, ProjectId = projectId, Status = StoryJobStatus.Queued, ContentHash = version.ContentHash };
        _context.StoryRenders.Add(render);
        await _context.SaveChangesAsync(ct);
        _queue.Enqueue(render.Id, tenantId);
        return ToDto(render, null);
    }

    public async Task<IReadOnlyList<StoryRenderDto>> ListRendersAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        await ProjectAsync(tenantId, projectId, ct);
        var renders = await _context.StoryRenders.IgnoreQueryFilters().Where(r => r.ProjectId == projectId).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var assetIds = renders.Where(r => r.OutputAssetId != null).Select(r => r.OutputAssetId!.Value).ToList();
        var assets = await _context.StoryMediaAssets.IgnoreQueryFilters().Where(a => assetIds.Contains(a.Id)).ToListAsync(ct);
        return renders.Select(r => ToDto(r, assets.FirstOrDefault(a => a.Id == r.OutputAssetId))).ToList();
    }

    public async Task<Stream?> OpenFinalAsync(Guid tenantId, Guid renderId, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var render = await _context.StoryRenders.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == renderId && r.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Render", renderId);
        return await OpenAssetAsync(render.OutputAssetId, ct);
    }

    public async Task<Stream?> OpenClipAsync(Guid tenantId, Guid sceneId, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var scene = await _context.StoryScenes.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == sceneId && s.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Scene", sceneId);
        return await OpenAssetAsync(scene.ClipAssetId, ct);
    }

    public async Task RunAsync(Guid renderId, CancellationToken ct = default)
    {
        var render = await _context.StoryRenders.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == renderId, ct);
        if (render is null || render.Status != StoryJobStatus.Queued) return;

        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstAsync(p => p.Id == render.ProjectId, ct);
        var dir = Path.Combine(Path.GetTempPath(), $"story-render-{renderId:N}");
        Directory.CreateDirectory(dir);
        render.Status = StoryJobStatus.Processing;
        await _context.SaveChangesAsync(ct);

        try
        {
            var scenes = await _context.StoryScenes.IgnoreQueryFilters()
                .Where(s => s.ProjectId == project.Id && s.ScriptVersionId == project.CurrentScriptVersionId).OrderBy(s => s.Index).ToListAsync(ct);
            if (scenes.Count == 0 || scenes.Any(s => s.Status != StorySceneStatus.Completed || s.ClipAssetId == null))
                throw new InvalidOperationException("A scene has no finished clip.");

            var ids = scenes.Select(s => s.Id).ToList();
            var lines = await _context.StoryDialogueLines.IgnoreQueryFilters().Where(l => ids.Contains(l.SceneId)).ToListAsync(ct);

            // Pull each clip down from storage, and lay the dialogue over the real clip lengths.
            var clipPaths = new List<string>();
            var timing = new List<(int, int, IReadOnlyList<StoryDialogueLine>)>();
            var offset = 0;
            foreach (var scene in scenes)
            {
                await using var source = await OpenAssetAsync(scene.ClipAssetId, ct) ?? throw new InvalidOperationException($"The clip for scene {scene.Index} is missing from storage.");
                var local = Path.Combine(dir, $"scene-{scene.Index}.mp4");
                await using (var target = File.Create(local)) await source.CopyToAsync(target, ct);
                clipPaths.Add(local);

                var length = scene.ActualMilliseconds ?? scene.TargetSeconds * 1000;
                timing.Add((offset, length, lines.Where(l => l.SceneId == scene.Id).ToList()));
                offset += length;
            }

            var srt = Path.Combine(dir, "subtitles.srt");
            await File.WriteAllTextAsync(srt, SubtitleBuilder.Srt(SubtitleBuilder.Cues(timing)), new UTF8Encoding(false), ct);

            var output = Path.Combine(dir, "final.mp4");
            await _ffmpeg.MergeAsync(clipPaths, output, srt, ct);

            var probe = await _ffmpeg.ProbeAsync(output, ct);
            var verdict = FinalVideoRules.Check(probe, project.TargetSeconds);
            render.ValidationJson = JsonSerializer.Serialize(new { passed = verdict.Passed, problems = verdict.Problems, probe });
            if (!verdict.Passed)
            {
                render.Status = StoryJobStatus.Failed;
                render.Error = "The finished video does not meet the rules: " + string.Join(" ", verdict.Problems);
                await _context.SaveChangesAsync(ct);
                return;
            }

            await using var final = File.OpenRead(output);
            var key = await _store.SaveAsync(project.TenantId, $"stories/{project.Id}/final-{renderId:N}.mp4", final, "video/mp4", ct);
            var asset = new StoryMediaAsset
            {
                TenantId = project.TenantId, ProjectId = project.Id, Kind = StoryMediaKind.FinalVideo, StorageKey = key, ContentType = "video/mp4",
                SizeBytes = new FileInfo(output).Length, DurationMs = (int)(probe.DurationSeconds * 1000), Width = probe.Width, Height = probe.Height,
                FramesPerSecond = probe.FramesPerSecond
            };
            _context.StoryMediaAssets.Add(asset);
            render.OutputAssetId = asset.Id;
            render.Status = StoryJobStatus.Completed;
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Story render {RenderId} failed", renderId);
            render.Status = StoryJobStatus.Failed;
            render.Error = "The video could not be put together: " + ex.Message;
            await _context.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* temp files left behind are harmless */ }
        }
    }

    private async Task<Stream?> OpenAssetAsync(Guid? assetId, CancellationToken ct)
    {
        if (assetId is null) return null;
        var asset = await _context.StoryMediaAssets.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        return asset is null ? null : await _store.OpenReadAsync(asset.StorageKey, ct);
    }

    private async Task<StoryProject> ProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        return await _context.StoryProjects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Story project", projectId);
    }

    private static StoryRenderDto ToDto(StoryRender r, StoryMediaAsset? asset)
    {
        var problems = new List<string>();
        if (r.ValidationJson is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("problems", out var list)) problems.AddRange(list.EnumerateArray().Select(p => p.GetString() ?? string.Empty));
        }

        return new StoryRenderDto(r.Id, r.Status, r.Error, problems, asset?.DurationMs / 1000.0, r.OutputAssetId != null, r.CreatedAt);
    }
}
