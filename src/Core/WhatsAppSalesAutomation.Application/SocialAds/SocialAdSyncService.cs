using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.SocialAds;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.SocialAds;

public class SocialAdSyncService : ISocialAdSyncService
{
    public const int BackfillMonths = 24;
    public const int RecentDays = 35;

    /// <summary>A 60-day token is renewed once it has 10 days or fewer left, so many missed daily runs are tolerated.</summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromDays(10);

    /// <summary>Each request covers at most this many months, which keeps a single response (days x platforms) small.</summary>
    private const int ChunkMonths = 3;

    private readonly IApplicationDbContext _context;
    private readonly IMetaAdsClient _meta;
    private readonly ISecretProtector _protector;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<SocialAdSyncService> _logger;

    public SocialAdSyncService(
        IApplicationDbContext context, IMetaAdsClient meta, ISecretProtector protector, IDateTimeProvider clock, ILogger<SocialAdSyncService> logger)
    {
        _context = context;
        _meta = meta;
        _protector = protector;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var tenantIds = await _context.SocialAdConnections.IgnoreQueryFilters()
            .Where(c => c.Status == SocialAdConnectionStatus.Connected)
            .Select(c => c.TenantId)
            .ToListAsync(cancellationToken);

        var synced = 0;
        foreach (var tenantId in tenantIds)
        {
            try
            {
                await SyncTenantAsync(tenantId, backfill: false, cancellationToken);
                synced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SyncTenantAsync records Meta failures itself; this is anything unexpected (database, bug).
                _logger.LogError(ex, "Social ad sync failed for tenant {TenantId}", tenantId);
            }
        }

        return synced;
    }

    public async Task SyncTenantAsync(Guid tenantId, bool backfill, CancellationToken cancellationToken = default)
    {
        var connection = await _context.SocialAdConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

        if (connection is null || connection.Status != SocialAdConnectionStatus.Connected || string.IsNullOrEmpty(connection.AdAccountId))
            return;

        var now = _clock.UtcNow;
        var token = _protector.TryUnprotect(connection.AccessToken);
        if (token is null)
        {
            await MarkNeedsReconnectAsync(connection, "The stored Facebook login could not be read. Please connect again.", cancellationToken);
            return;
        }

        if (connection.TokenExpiresAt is { } expiresAt && expiresAt <= now)
        {
            await MarkNeedsReconnectAsync(connection, "The Facebook login has expired. Please connect again.", cancellationToken);
            return;
        }

        try
        {
            if (connection.TokenExpiresAt is { } soon && soon - now <= RefreshWindow)
            {
                var renewed = await _meta.RefreshTokenAsync(token, cancellationToken);
                token = renewed.AccessToken;
                connection.AccessToken = _protector.Protect(renewed.AccessToken);
                connection.TokenExpiresAt = renewed.ExpiresAtUtc;
            }

            var today = now.Date;
            var since = backfill
                ? new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(BackfillMonths - 1))
                : today.AddDays(-RecentDays);

            var rows = new List<MetaInsightRow>();
            foreach (var (from, to) in Chunks(since, today))
                rows.AddRange(await _meta.GetInsightsAsync(token, connection.AdAccountId, from, to, cancellationToken));

            await StoreAsync(connection, rows, since, today, cancellationToken);

            connection.LastSyncedAt = now;
            connection.LastSyncError = null;
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (MetaAuthException ex)
        {
            await MarkNeedsReconnectAsync(connection, ex.Message, cancellationToken);
        }
        catch (MetaApiException ex)
        {
            // The token may be fine (Meta busy, rate-limited): keep it connected and retry on the next run.
            connection.LastSyncError = Truncate(ex.Message);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Replaces what is stored for the window with what Meta returned: updated days are overwritten, new
    /// days added, and a stored day Meta no longer reports (a correction to nothing) removed.</summary>
    private async Task StoreAsync(
        SocialAdConnection connection, IReadOnlyList<MetaInsightRow> rows, DateTime since, DateTime until, CancellationToken cancellationToken)
    {
        var accountId = connection.AdAccountId!;
        var existing = await _context.SocialAdSpends.IgnoreQueryFilters()
            .Where(s => s.TenantId == connection.TenantId && s.Source == SocialAdSpend.SourceMeta && s.AdAccountId == accountId
                        && s.Date >= since && s.Date <= until)
            .ToListAsync(cancellationToken);
        var byKey = existing.ToDictionary(s => (s.Date.Date, s.Platform));

        var seen = new HashSet<(DateTime, string)>();
        foreach (var row in rows)
        {
            var key = (row.Date.Date, row.Platform);
            // Meta can split one platform's day across rows (for example by position); fold them together.
            if (seen.Add(key))
            {
                if (byKey.TryGetValue(key, out var stored))
                {
                    stored.Spend = row.Spend;
                    stored.Impressions = row.Impressions;
                    stored.Clicks = row.Clicks;
                    stored.Leads = row.Leads;
                    stored.CurrencyCode = connection.CurrencyCode ?? string.Empty;
                }
                else
                {
                    var added = new SocialAdSpend
                    {
                        TenantId = connection.TenantId,
                        Source = SocialAdSpend.SourceMeta,
                        AdAccountId = accountId,
                        Date = row.Date.Date,
                        Platform = row.Platform,
                        Spend = row.Spend,
                        Impressions = row.Impressions,
                        Clicks = row.Clicks,
                        Leads = row.Leads,
                        CurrencyCode = connection.CurrencyCode ?? string.Empty,
                    };
                    _context.SocialAdSpends.Add(added);
                    byKey[key] = added;
                }
            }
            else
            {
                var stored = byKey[key];
                stored.Spend += row.Spend;
                stored.Impressions += row.Impressions;
                stored.Clicks += row.Clicks;
                stored.Leads += row.Leads;
            }
        }

        _context.SocialAdSpends.RemoveRange(existing.Where(e => !seen.Contains((e.Date.Date, e.Platform))));
    }

    private async Task MarkNeedsReconnectAsync(SocialAdConnection connection, string reason, CancellationToken cancellationToken)
    {
        connection.Status = SocialAdConnectionStatus.NeedsReconnect;
        connection.LastSyncError = Truncate(reason);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Consecutive windows of at most <see cref="ChunkMonths"/> months covering since..until inclusive.</summary>
    public static IEnumerable<(DateTime From, DateTime To)> Chunks(DateTime since, DateTime until)
    {
        var from = since.Date;
        while (from <= until.Date)
        {
            var to = from.AddMonths(ChunkMonths).AddDays(-1);
            if (to > until.Date)
                to = until.Date;
            yield return (from, to);
            from = to.AddDays(1);
        }
    }

    private static string Truncate(string message) => message.Length <= 500 ? message : message[..500];
}
