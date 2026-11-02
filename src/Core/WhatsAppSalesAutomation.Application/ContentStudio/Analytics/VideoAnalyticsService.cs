using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Publishing;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Analytics;

/// <summary>
/// How the published videos are doing. Once a day the platform is asked for the views, likes and comments of every video published in the last
/// 180 days - one call per 50 videos per account - and each answer is added as a snapshot, so the history of a video is kept, not just its latest
/// numbers. A video the platform stops returning is marked as such and keeps the last numbers it gave. A sign-in the platform no longer accepts
/// marks the account for reconnecting; a temporary problem is simply tried again tomorrow. Nothing here ever writes to the platform.
/// </summary>
public sealed class VideoAnalyticsService : IVideoAnalyticsService
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly IYouTubeClient _youtube;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<VideoAnalyticsService> _logger;

    public VideoAnalyticsService(IApplicationDbContext db, ISecretProtector protector, IYouTubeClient youtube, IDateTimeProvider clock, ILogger<VideoAnalyticsService> logger)
    {
        _db = db;
        _protector = protector;
        _youtube = youtube;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Guid>> TenantsToSyncAsync(CancellationToken ct = default)
    {
        var since = _clock.UtcNow.AddDays(-AnalyticsLimits.MetricsWindowDays);
        return await _db.Publications.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Status == PublicationStatus.Published && p.ExternalPostId != null && p.PublishedAtUtc >= since)
            .Select(p => p.TenantId).Distinct().ToListAsync(ct);
    }

    public async Task<MetricsSyncResult> SyncTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var since = now.AddDays(-AnalyticsLimits.MetricsWindowDays);
        var stale = now - AnalyticsLimits.SyncEvery;
        var due = await _db.Publications.Where(p =>
            p.TenantId == tenantId && p.Status == PublicationStatus.Published && p.ExternalPostId != null && p.Platform == SocialPlatform.YouTube && p.PublishedAtUtc >= since
            && (p.MetricsRefreshedAtUtc == null || p.MetricsRefreshedAtUtc < stale)).ToListAsync(ct);
        return await AskAsync(tenantId, due, ct);
    }

    public async Task<PublicationPerformanceDto> RefreshAsync(Guid tenantId, Guid publicationId, CancellationToken ct = default)
    {
        var publication = await _db.Publications.FirstOrDefaultAsync(p => p.Id == publicationId && p.TenantId == tenantId, ct) ?? throw new NotFoundException("Publication", publicationId);
        if (publication.Status != PublicationStatus.Published || string.IsNullOrEmpty(publication.ExternalPostId))
            throw new ConflictException("There are no numbers yet: this video is not on YouTube.");

        var now = _clock.UtcNow;
        if (publication.MetricsRefreshedAtUtc is { } last && now - last < AnalyticsLimits.ManualRefreshEvery)
        {
            var wait = (int)Math.Ceiling((AnalyticsLimits.ManualRefreshEvery - (now - last)).TotalMinutes);
            throw new ConflictException($"These numbers were updated {(int)(now - last).TotalMinutes} minute(s) ago. YouTube's counts move slowly - try again in {wait} minute(s).");
        }

        var result = await AskAsync(tenantId, new List<Publication> { publication }, ct);
        if (result.Errors > 0 || result.Skipped > 0)
            throw new ConflictException("YouTube could not be asked right now. If your account needs connecting again, do that under Publishing accounts; otherwise try again later.");

        return (await GetSummaryAsync(tenantId, ct)).Items.FirstOrDefault(i => i.Id == publicationId) ?? throw new NotFoundException("Publication", publicationId);
    }

    /// <summary>Asks the platform about these posts, one account at a time, and records what comes back.</summary>
    private async Task<MetricsSyncResult> AskAsync(Guid tenantId, List<Publication> publications, CancellationToken ct)
    {
        int updated = 0, missing = 0, skipped = 0, errors = 0;
        var now = _clock.UtcNow;

        foreach (var group in publications.GroupBy(p => p.ConnectionId))
        {
            var connection = await _db.SocialConnections.FirstOrDefaultAsync(c => c.Id == group.Key && c.TenantId == tenantId, ct);
            if (connection is null || connection.Status != SocialConnectionStatus.Connected)
            {
                skipped += group.Count();
                continue;
            }

            try
            {
                var token = await SocialTokens.FreshAccessTokenAsync(_db, _protector, _youtube, _clock, connection, ct);
                foreach (var chunk in group.Chunk(50))
                {
                    var stats = await _youtube.GetVideoStatisticsAsync(token, chunk.Select(p => p.ExternalPostId!).ToList(), ct);
                    foreach (var publication in chunk)
                    {
                        publication.MetricsRefreshedAtUtc = now;
                        if (stats.TryGetValue(publication.ExternalPostId!, out var s))
                        {
                            _db.PublicationMetricSnapshots.Add(new PublicationMetricSnapshot
                            {
                                TenantId = tenantId, PublicationId = publication.Id, CapturedAtUtc = now, Views = s.Views, Likes = s.Likes, Comments = s.Comments
                            });
                            publication.MissingOnPlatformSinceUtc = null;
                            updated++;
                        }
                        else
                        {
                            publication.MissingOnPlatformSinceUtc ??= now;
                            missing++;
                        }
                    }
                }

                connection.LastUsedAtUtc = now;
            }
            catch (SocialProviderException ex)
            {
                errors += group.Count();
                if (ex.Failure == SocialFailure.NeedsReconnect)
                {
                    connection.Status = SocialConnectionStatus.NeedsReconnect;
                    connection.LastError = ex.Message;
                    connection.UpdatedAt = now;
                }
                else
                {
                    _logger.LogWarning("The platform could not be asked about published videos for tenant {TenantId}: {Reason}", tenantId, ex.Message);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                errors += group.Count();
                _logger.LogWarning(ex, "The platform could not be reached to ask about published videos for tenant {TenantId}.", tenantId);
            }
        }

        await _db.SaveChangesAsync(ct);
        return new MetricsSyncResult(updated, missing, skipped, errors);
    }

    public async Task<AnalyticsSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var month = now.AddDays(-30);

        var published = await _db.Publications.AsNoTracking().Where(p => p.TenantId == tenantId && p.Status == PublicationStatus.Published)
            .OrderByDescending(p => p.PublishedAtUtc).Take(AnalyticsLimits.MaxItems).ToListAsync(ct);
        var ids = published.Select(p => p.Id).ToList();
        var snapshots = await _db.PublicationMetricSnapshots.AsNoTracking().Where(m => m.TenantId == tenantId && ids.Contains(m.PublicationId)).OrderBy(m => m.CapturedAtUtc).ToListAsync(ct);

        var items = published.Select(p =>
        {
            var series = snapshots.Where(m => m.PublicationId == p.Id).ToList();
            var latest = series.LastOrDefault();
            return new PublicationPerformanceDto(
                p.Id, p.VersionId, p.Title, p.AccountName, p.Privacy, p.Url, p.PublishedAtUtc, latest?.Views ?? 0, latest?.Likes ?? 0, latest?.Comments ?? 0, p.MetricsRefreshedAtUtc,
                p.MissingOnPlatformSinceUtc != null, series.TakeLast(AnalyticsLimits.SeriesPoints).Select(m => new MetricPointDto(m.CapturedAtUtc, m.Views, m.Likes, m.Comments)).ToList());
        }).ToList();

        var waiting = await _db.Publications.AsNoTracking().CountAsync(p => p.TenantId == tenantId && (p.Status == PublicationStatus.Scheduled || p.Status == PublicationStatus.Uploading), ct);
        var failed = await _db.Publications.AsNoTracking().CountAsync(p => p.TenantId == tenantId && p.Status == PublicationStatus.Failed && p.CreatedAt >= month, ct);
        var funnel = new FunnelDto(
            await _db.ContentVersions.AsNoTracking().CountAsync(v => v.TenantId == tenantId && !v.IsSimulated && v.CreatedAt >= month, ct),
            await _db.ContentVersions.AsNoTracking().CountAsync(v => v.TenantId == tenantId && v.Status == ContentVersionStatus.Approved && v.ReviewedAt >= month, ct),
            await _db.RenderJobs.AsNoTracking().CountAsync(j => j.TenantId == tenantId && !j.IsSimulated && j.Status == RenderJobStatus.Succeeded && j.FinishedAt >= month, ct),
            await _db.Publications.AsNoTracking().CountAsync(p => p.TenantId == tenantId && p.Status == PublicationStatus.Published && p.PublishedAtUtc >= month, ct));

        return new AnalyticsSummaryDto(
            await _db.Publications.AsNoTracking().CountAsync(p => p.TenantId == tenantId && p.Status == PublicationStatus.Published, ct), waiting, failed,
            items.Sum(i => i.Views), items.Sum(i => i.Likes), items.Sum(i => i.Comments), items.Max(i => i.RefreshedAtUtc), funnel, items);
    }
}
