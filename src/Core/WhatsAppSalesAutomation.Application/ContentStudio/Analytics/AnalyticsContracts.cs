namespace WhatsAppSalesAutomation.Application.ContentStudio.Analytics;

public static class AnalyticsLimits
{
    /// <summary>A published video is followed for this long; older ones keep their last numbers.</summary>
    public const int MetricsWindowDays = 180;

    /// <summary>A post is not asked about again within this long by the daily sync (it runs about once a day).</summary>
    public static readonly TimeSpan SyncEvery = TimeSpan.FromHours(20);

    /// <summary>The "refresh now" button is allowed this often per post: YouTube's numbers move slowly and every call spends quota.</summary>
    public static readonly TimeSpan ManualRefreshEvery = TimeSpan.FromMinutes(15);

    public const int SeriesPoints = 30;
    public const int MaxItems = 50;
}

public sealed record MetricPointDto(DateTime At, long Views, long Likes, long Comments);

/// <param name="MissingOnPlatform">YouTube no longer returns the video (deleted there, or made unavailable): the numbers are the last it gave.</param>
public sealed record PublicationPerformanceDto(
    Guid Id, Guid VersionId, string Title, string AccountName, string Privacy, string? Url, DateTime? PublishedAtUtc, long Views, long Likes, long Comments,
    DateTime? RefreshedAtUtc, bool MissingOnPlatform, IReadOnlyList<MetricPointDto> Series);

/// <summary>What happened in the last 30 days, step by step: scripts written, approved, videos made, videos published.</summary>
public sealed record FunnelDto(int ScriptsWritten, int ScriptsApproved, int VideosMade, int VideosPublished);

public sealed record AnalyticsSummaryDto(
    int Published, int WaitingOrSending, int FailedLast30Days, long Views, long Likes, long Comments, DateTime? LastUpdatedUtc, FunnelDto Last30Days,
    IReadOnlyList<PublicationPerformanceDto> Items);

public sealed record MetricsSyncResult(int Updated, int Missing, int Skipped, int Errors);

public interface IVideoAnalyticsService
{
    /// <summary>The tenants that have published videos still being followed.</summary>
    Task<IReadOnlyList<Guid>> TenantsToSyncAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the platform how each recently published video is doing and records a snapshot. Safe to repeat: a post asked about in the last day is left alone.</summary>
    Task<MetricsSyncResult> SyncTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<AnalyticsSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Asks about one post now. Refused if it was asked about a few minutes ago.</summary>
    Task<PublicationPerformanceDto> RefreshAsync(Guid tenantId, Guid publicationId, CancellationToken cancellationToken = default);
}

public sealed record StudioMaintenanceResult(int StuckRenders, int StuckPublications, int RequeuedPublications, int VideosRemoved, int SignInsRemoved)
{
    public int Total => StuckRenders + StuckPublications + RequeuedPublications + VideosRemoved + SignInsRemoved;
}

public interface IStudioMaintenanceService
{
    /// <summary>The tenants that have any studio work or files to look after.</summary>
    Task<IReadOnlyList<Guid>> TenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Housekeeping for one tenant: ends renders and publications that stopped moving (returning charges, deleting leftover voice files), sends again
    /// what was lost from the queue, deletes video files past their retention, and removes sign-ins that have needed reconnecting for too long.
    /// </summary>
    Task<StudioMaintenanceResult> RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
