using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Publishing;
using WhatsAppSalesAutomation.Application.ContentStudio.Video;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Analytics;

/// <summary>
/// Housekeeping that keeps the studio from leaking money or files. A render or a publication that stopped moving (a restart, a lost message)
/// is ended with a reason - a render's charges are returned and its voice files deleted; a publication that was mid-upload is marked as possibly
/// on the channel, never retried blindly; one that never started is sent again (safe: the sender ignores what is already done). Finished video
/// files are deleted once past their retention, unless a publication still needs them; a sign-in that has needed reconnecting for too long is
/// revoked and deleted. Every step is separate and idempotent, so a failed step is simply tried again tomorrow.
/// </summary>
public sealed class StudioMaintenanceService : IStudioMaintenanceService
{
    private static readonly TimeSpan StuckAfterRender = TimeSpan.FromMinutes(30); // beyond the render's own time limit
    private static readonly TimeSpan StuckPublication = TimeSpan.FromHours(2);
    private static readonly RenderJobStatus[] ActiveRender = { RenderJobStatus.Queued, RenderJobStatus.Preparing, RenderJobStatus.Rendering };

    private readonly IApplicationDbContext _db;
    private readonly ITenantDocumentStore _store;
    private readonly IStudioMeteringService _metering;
    private readonly IPublicationQueue _queue;
    private readonly IYouTubeClient _youtube;
    private readonly ISecretProtector _protector;
    private readonly IDateTimeProvider _clock;
    private readonly IOptionsSnapshot<StudioRetentionOptions> _options;
    private readonly ILogger<StudioMaintenanceService> _logger;

    public StudioMaintenanceService(
        IApplicationDbContext db, ITenantDocumentStore store, IStudioMeteringService metering, IPublicationQueue queue, IYouTubeClient youtube, ISecretProtector protector,
        IDateTimeProvider clock, IOptionsSnapshot<StudioRetentionOptions> options, ILogger<StudioMaintenanceService> logger)
    {
        _db = db;
        _store = store;
        _metering = metering;
        _queue = queue;
        _youtube = youtube;
        _protector = protector;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Guid>> TenantsAsync(CancellationToken ct = default)
    {
        var renders = await _db.RenderJobs.IgnoreQueryFilters().AsNoTracking().Select(j => j.TenantId).Distinct().ToListAsync(ct);
        var publications = await _db.Publications.IgnoreQueryFilters().AsNoTracking().Select(p => p.TenantId).Distinct().ToListAsync(ct);
        var connections = await _db.SocialConnections.IgnoreQueryFilters().AsNoTracking().Select(c => c.TenantId).Distinct().ToListAsync(ct);
        return renders.Union(publications).Union(connections).ToList();
    }

    public async Task<StudioMaintenanceResult> RunForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var stuckRenders = await Step("stuck renders", () => EndStuckRendersAsync(tenantId, ct));
        var (stuckPublications, requeued) = await Step("stuck publications", () => FixStuckPublicationsAsync(tenantId, ct), (0, 0));
        var videos = await Step("old video files", () => RemoveOldVideosAsync(tenantId, ct));
        var signIns = await Step("dead sign-ins", () => RemoveDeadSignInsAsync(tenantId, ct));
        return new StudioMaintenanceResult(stuckRenders, stuckPublications, requeued, videos, signIns);
    }

    private async Task<T> Step<T>(string name, Func<Task<T>> run, T fallback = default!)
    {
        try
        {
            return await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Studio maintenance could not finish {Step}.", name);
            return fallback;
        }
    }

    // ----- renders that stopped moving -------------------------------------------------------------------------------------------

    private async Task<int> EndStuckRendersAsync(Guid tenantId, CancellationToken ct)
    {
        var cutoff = _clock.UtcNow - RenderLimits.MaxDuration - StuckAfterRender;
        var stuck = (await _db.RenderJobs.Where(j => j.TenantId == tenantId && ActiveRender.Contains(j.Status)).ToListAsync(ct)).Where(j => (j.StartedAt ?? j.CreatedAt) < cutoff).ToList();

        foreach (var job in stuck)
        {
            foreach (var key in ReadKeys(job.AudioKeysJson))
                await DeleteQuietlyAsync(key);
            job.AudioKeysJson = "[]";
            foreach (var clip in await _db.RenderClips.Where(c => c.JobId == job.Id && c.OutputStorageKey != null).ToListAsync(ct))
            {
                await DeleteQuietlyAsync(clip.OutputStorageKey!);
                clip.OutputStorageKey = null;
            }

            job.Status = RenderJobStatus.Failed;
            job.Stage = "Stopped: it took far too long";
            job.Error = "The video stopped making progress and was ended. Nothing was charged - try again.";
            job.FinishedAt = _clock.UtcNow;
            job.UpdatedAt = _clock.UtcNow;
            foreach (var id in new[] { job.RenderActivityId, job.VoiceActivityId })
                if (id is { } activity)
                    await _metering.RefundAsync(activity, "The video stopped making progress.", CancellationToken.None);
        }

        if (stuck.Count > 0)
            await _db.SaveChangesAsync(ct);
        return stuck.Count;
    }

    // ----- publications that stopped moving --------------------------------------------------------------------------------------

    private async Task<(int Stuck, int Requeued)> FixStuckPublicationsAsync(Guid tenantId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var cutoff = now - StuckPublication;
        var rows = await _db.Publications.Where(p => p.TenantId == tenantId && (p.Status == PublicationStatus.Uploading || p.Status == PublicationStatus.Scheduled)).ToListAsync(ct);

        var stuck = 0;
        var requeue = new List<Publication>();
        foreach (var publication in rows)
        {
            if (publication.Status == PublicationStatus.Uploading && (publication.StartedAtUtc ?? publication.CreatedAt) < cutoff)
            {
                // It was being sent and nothing came back: the video may be there, so it is never retried by itself.
                publication.Status = PublicationStatus.Failed;
                publication.LastError = "The sending stopped without an answer, so the video may or may not be on YouTube. Look at your channel before trying again.";
                publication.UpdatedAt = now;
                stuck++;
            }
            else if (publication.Status == PublicationStatus.Scheduled && (publication.NextRetryAtUtc ?? publication.ScheduledAtUtc ?? publication.CreatedAt) < cutoff)
            {
                requeue.Add(publication); // due long ago and still waiting: its queued message was lost
            }
        }

        await _db.SaveChangesAsync(ct);
        foreach (var publication in requeue)
            _queue.Enqueue(publication.Id, tenantId, null);
        return (stuck, requeue.Count);
    }

    // ----- files and sign-ins past their time ------------------------------------------------------------------------------------

    private async Task<int> RemoveOldVideosAsync(Guid tenantId, CancellationToken ct)
    {
        if (StudioRetentionOptions.Effective(_options.Value.VideoDays) is not { } days)
            return 0;

        var cutoff = _clock.UtcNow.AddDays(-days);
        var waiting = (await _db.Publications.AsNoTracking().Where(p => p.TenantId == tenantId && (p.Status == PublicationStatus.Scheduled || p.Status == PublicationStatus.Uploading))
            .Select(p => p.RenderJobId).ToListAsync(ct)).ToHashSet();
        var old = (await _db.RenderJobs.Where(j => j.TenantId == tenantId && j.OutputStorageKey != null && j.FinishedAt != null && j.FinishedAt < cutoff).ToListAsync(ct))
            .Where(j => !waiting.Contains(j.Id)).ToList();

        var folder = _store.FolderOf(tenantId);
        foreach (var job in old)
        {
            if (job.OutputStorageKey!.Contains(folder, StringComparison.Ordinal))
                await _store.DeleteAsync(job.OutputStorageKey, ct);
            job.OutputStorageKey = null;
            job.OutputBytes = 0;
            job.Stage = $"The video file was removed after {days} days";
            job.UpdatedAt = _clock.UtcNow;
        }

        if (old.Count > 0)
            await _db.SaveChangesAsync(ct);
        return old.Count;
    }

    private async Task<int> RemoveDeadSignInsAsync(Guid tenantId, CancellationToken ct)
    {
        if (StudioRetentionOptions.Effective(_options.Value.DeadSignInDays) is not { } days)
            return 0;

        var cutoff = _clock.UtcNow.AddDays(-days);
        var dead = (await _db.SocialConnections.Where(c => c.TenantId == tenantId && c.Status == SocialConnectionStatus.NeedsReconnect).ToListAsync(ct))
            .Where(c => (c.UpdatedAt ?? c.CreatedAt) < cutoff).ToList();

        foreach (var connection in dead)
        {
            var waiting = await _db.Publications.Where(p => p.TenantId == tenantId && p.ConnectionId == connection.Id && p.Status == PublicationStatus.Scheduled).ToListAsync(ct);
            foreach (var publication in waiting)
            {
                publication.Status = PublicationStatus.Cancelled;
                publication.LastError = "The account was removed because it needed to be connected again for too long.";
                publication.NextRetryAtUtc = null;
                publication.UpdatedAt = _clock.UtcNow;
            }

            var refresh = _protector.TryUnprotect(connection.EncryptedRefreshToken);
            _db.SocialConnections.Remove(connection);
            await _db.SaveChangesAsync(ct);

            if (refresh is not null)
            {
                try
                {
                    await _youtube.RevokeAsync(refresh, ct);
                }
                catch (Exception ex) when (ex is SocialProviderException or HttpRequestException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Google did not confirm revoking a dead sign-in; the saved tokens are deleted regardless.");
                }
            }
        }

        return dead.Count;
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

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
}
