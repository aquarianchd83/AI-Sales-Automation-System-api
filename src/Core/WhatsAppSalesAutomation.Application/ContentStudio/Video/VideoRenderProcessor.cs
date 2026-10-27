using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Video;

/// <summary>
/// The background half of making a video, run in the tenant's scope. Step one speaks each scene, keeps the voice files privately, and hands the
/// renderer a plan with short-lived single-file addresses for the pictures and the voice; step two (repeated) asks the renderer how it is going
/// and, when it is done, copies the finished video into the tenant's private folder. Every way out - done, failed, cancelled, timed out - ends
/// the job once: the voice files are deleted, a failure or cancellation returns the charge, and the reason is written for the tenant. Nothing
/// here retries by itself: a failed video is asked for again by the tenant, so it is never paid for twice behind their back.
/// </summary>
public sealed class VideoRenderProcessor : IVideoRenderProcessor
{
    private readonly IApplicationDbContext _db;
    private readonly ITenantDocumentStore _store;
    private readonly ITenantFileLinks _links;
    private readonly IVoiceSynthesizer _voice;
    private readonly IVideoRenderer _renderer;
    private readonly IVideoRenderQueue _queue;
    private readonly IStudioMeteringService _metering;
    private readonly IDateTimeProvider _clock;
    private readonly IOptionsSnapshot<RenderOptions> _options;
    private readonly ILogger<VideoRenderProcessor> _logger;

    public VideoRenderProcessor(
        IApplicationDbContext db, ITenantDocumentStore store, ITenantFileLinks links, IVoiceSynthesizer voice, IVideoRenderer renderer, IVideoRenderQueue queue,
        IStudioMeteringService metering, IDateTimeProvider clock, IOptionsSnapshot<RenderOptions> options, ILogger<VideoRenderProcessor> logger)
    {
        _db = db;
        _store = store;
        _links = links;
        _voice = voice;
        _renderer = renderer;
        _queue = queue;
        _metering = metering;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    // ----- step one: speak, gather, submit --------------------------------------------------------------------------------------------

    public async Task StartAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.RenderJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != RenderJobStatus.Queued)
            return; // already handled: a repeated message must not do the work twice

        if (job.CancelRequested)
        {
            await EndAsync(job, RenderJobStatus.Cancelled, "Cancelled by you", null, ct);
            return;
        }

        job.Status = RenderJobStatus.Preparing;
        job.StartedAt = _clock.UtcNow;
        job.Progress = 5;
        job.Stage = job.IsSimulated ? "Running a test render" : "Speaking the narration";
        await SaveAsync(job, ct);

        try
        {
            if (job.IsSimulated)
            {
                // Test mode: the steps ran, nothing was spoken or rendered, and no video is pretended.
                job.OutputSeconds = await _db.ContentScenes.Where(s => s.VersionId == job.VersionId).SumAsync(s => (double)s.DurationSeconds, ct);
                await EndAsync(job, RenderJobStatus.Succeeded, "Test render finished - the platform is in test mode, so no video was made", null, ct);
                return;
            }

            var plan = await PrepareAsync(job, ct);
            if (plan is null)
                return; // cancelled while preparing; already ended

            job.ExternalRenderId = await _renderer.SubmitAsync(plan, ct);
            job.Status = RenderJobStatus.Rendering;
            job.Progress = 45;
            job.Stage = "Making the video";
            await SaveAsync(job, ct);
            _queue.SchedulePoll(job.Id, job.TenantId, RenderLimits.PollEvery);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(job, ex, ct);
        }
    }

    private async Task<RenderPlan?> PrepareAsync(RenderJob job, CancellationToken ct)
    {
        var scenes = await _db.ContentScenes.AsNoTracking().Where(s => s.VersionId == job.VersionId).OrderBy(s => s.Order).ToListAsync(ct);
        var assetIds = scenes.Where(s => s.ChosenAssetId != null).Select(s => s.ChosenAssetId!.Value).Distinct().ToList();
        var assets = await _db.StudioAssets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToListAsync(ct);
        var folder = _store.FolderOf(job.TenantId);

        var keys = new List<string>();
        var planned = new List<RenderScene>();
        double start = 0;
        for (var i = 0; i < scenes.Count; i++)
        {
            if (await CancelRequestedAsync(job.Id, ct))
            {
                await EndAsync(job, RenderJobStatus.Cancelled, "Cancelled by you", null, ct);
                return null;
            }

            var scene = scenes[i];
            var asset = assets.FirstOrDefault(a => a.Id == scene.ChosenAssetId) ?? throw new VideoProviderException($"Scene {scene.Order} has no picture any more. Choose one and try again.");
            if (!asset.StorageKey.Contains(folder, StringComparison.Ordinal))
                throw new VideoProviderException($"The picture of scene {scene.Order} could not be found.");

            var clip = await _voice.SynthesizeAsync(scene.Narration, job.LanguageCode, job.VoiceGender, ct);
            await using (var wav = new MemoryStream(clip.Wav))
                keys.Add(await _store.SaveAsync(job.TenantId, $"studio/audio/{job.Id:N}/scene-{scene.Order}.wav", wav, "audio/wav", ct));
            job.AudioKeysJson = JsonSerializer.Serialize(keys);

            // The scene lasts as long as the script planned, or as long as the voice needs (plus a breath), whichever is longer.
            var length = Math.Max(scene.DurationSeconds, Math.Round(clip.Seconds + 0.4, 1));
            planned.Add(new RenderScene(
                _links.CreateTemporaryReadUrl(job.TenantId, asset.StorageKey, RenderLimits.LinkLifetime), _links.CreateTemporaryReadUrl(job.TenantId, keys[^1], RenderLimits.LinkLifetime),
                start, length, scene.OnScreenText));
            start += length;

            job.Progress = 5 + (int)(35.0 * (i + 1) / scenes.Count);
            await SaveAsync(job, ct);
        }

        job.OutputSeconds = start;
        return new RenderPlan(job.AspectRatio, planned);
    }

    // ----- step two: ask, and collect --------------------------------------------------------------------------------------------------

    public async Task PollAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.RenderJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != RenderJobStatus.Rendering || string.IsNullOrEmpty(job.ExternalRenderId))
            return;

        if (job.CancelRequested)
        {
            // The renderer cannot be told to stop; its result is simply never collected.
            await EndAsync(job, RenderJobStatus.Cancelled, "Cancelled by you", null, ct);
            return;
        }

        if (job.StartedAt is { } started && _clock.UtcNow - started > RenderLimits.MaxDuration)
        {
            await EndAsync(job, RenderJobStatus.Failed, "The video took too long and was stopped", "The video service did not finish in time. Nothing was charged - try again.", ct);
            return;
        }

        RenderStatus status;
        try
        {
            status = await _renderer.GetStatusAsync(job.ExternalRenderId, ct);
            job.PollFailures = 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or VideoProviderException)
        {
            job.PollFailures++;
            if (job.PollFailures >= RenderLimits.MaxPollFailures)
            {
                await FailAsync(job, ex, ct);
                return;
            }

            await SaveAsync(job, ct);
            _queue.SchedulePoll(job.Id, job.TenantId, RenderLimits.PollEvery);
            return;
        }

        switch (status.Phase)
        {
            case RenderPhase.Failed:
                await FailAsync(job, new VideoProviderException(status.Error is { Length: > 0 } e ? $"The video service could not make the video: {Trim(e, 300)}" : "The video service could not make the video."), ct);
                return;

            case RenderPhase.Done:
                await CollectAsync(job, status, ct);
                return;

            default:
                job.Progress = Math.Clamp(45 + (int)(status.Percent * 0.45), 45, 90);
                job.Stage = "Making the video";
                await SaveAsync(job, ct);
                _queue.SchedulePoll(job.Id, job.TenantId, RenderLimits.PollEvery);
                return;
        }
    }

    private async Task CollectAsync(RenderJob job, RenderStatus status, CancellationToken ct)
    {
        job.Progress = 92;
        job.Stage = "Saving the video";
        await SaveAsync(job, ct);
        try
        {
            if (string.IsNullOrEmpty(status.Url))
                throw new VideoProviderException("The video service finished but gave no video.");

            // A temporary file that deletes itself when closed, so a failure here leaves nothing behind on this machine.
            await using var file = new FileStream(Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.mp4"), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            await _renderer.DownloadAsync(status.Url, RenderLimits.MaxVideoBytes, file, ct);
            if (file.Length == 0)
                throw new VideoProviderException("The video service returned an empty video.");

            file.Position = 0;
            job.OutputStorageKey = await _store.SaveAsync(job.TenantId, $"studio/renders/{job.Id:N}.mp4", file, "video/mp4", ct);
            job.OutputBytes = file.Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(job, ex, ct);
            return;
        }

        await EndAsync(job, RenderJobStatus.Succeeded, "Your video is ready", null, ct);
    }

    // ----- ending a job ------------------------------------------------------------------------------------------------------------

    private async Task FailAsync(RenderJob job, Exception ex, CancellationToken ct)
    {
        string message;
        switch (ex)
        {
            case VideoProviderException v:
                message = v.Message;
                break;
            case StorageUnavailableException s:
                message = s.Message;
                break;
            case HttpRequestException or TaskCanceledException:
                message = "The video or voice service could not be reached.";
                _logger.LogWarning(ex, "Render {JobId} could not reach a service.", job.Id);
                break;
            default:
                message = "Something went wrong while making the video.";
                _logger.LogError(ex, "Render {JobId} failed.", job.Id);
                break;
        }

        // A video that was already saved is removed again: a failed job holds no file.
        if (!string.IsNullOrEmpty(job.OutputStorageKey))
        {
            await DeleteQuietlyAsync(job.OutputStorageKey);
            job.OutputStorageKey = null;
            job.OutputBytes = 0;
        }

        await EndAsync(job, RenderJobStatus.Failed, "Could not make the video", $"{message} Nothing was charged - try again.", ct);
    }

    private async Task EndAsync(RenderJob job, RenderJobStatus status, string stage, string? error, CancellationToken ct)
    {
        job.Status = status;
        job.Stage = stage;
        job.Error = error is null ? null : Trim(error, 600);
        job.Progress = status == RenderJobStatus.Succeeded ? 100 : job.Progress;
        job.FinishedAt = _clock.UtcNow;
        job.UpdatedAt = _clock.UtcNow;

        foreach (var key in ReadKeys(job.AudioKeysJson))
            await DeleteQuietlyAsync(key);
        job.AudioKeysJson = "[]";

        foreach (var id in new[] { job.RenderActivityId, job.VoiceActivityId })
        {
            if (id is not { } activity)
                continue;
            if (status == RenderJobStatus.Succeeded)
                await _metering.CompleteAsync(activity, CancellationToken.None);
            else
                await _metering.RefundAsync(activity, status == RenderJobStatus.Cancelled ? "Cancelled by the tenant." : "The video could not be made.", CancellationToken.None);
        }

        await _db.SaveChangesAsync(CancellationToken.None);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private async Task SaveAsync(RenderJob job, CancellationToken ct)
    {
        job.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<bool> CancelRequestedAsync(Guid jobId, CancellationToken ct) =>
        await _db.RenderJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.CancelRequested).FirstOrDefaultAsync(ct);

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await _store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A studio file could not be removed from storage.");
        }
    }

    private static List<string> ReadKeys(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];
}
