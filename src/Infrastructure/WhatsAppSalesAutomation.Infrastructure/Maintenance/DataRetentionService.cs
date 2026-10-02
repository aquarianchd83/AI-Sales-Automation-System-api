using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.Maintenance;

/// <summary>
/// Removes what has outlived its retention period. Lives in Infrastructure because it uses set-based deletes
/// (<c>ExecuteDeleteAsync</c>), which skip the change tracker: nothing is loaded, and - for the audit trail - the append-only guard in the
/// save interceptor (which watches tracked entities) is not what stands in the way. The age cut-off is.
///
/// Works across every tenant (the global query filters are ignored): retention is a platform rule, not something one tenant's request
/// context should narrow. Deletes in batches of ids so one pass never holds a long lock on a big table, and a failure in one kind of data
/// does not stop the others.
/// </summary>
public class DataRetentionService : IDataRetentionService
{
    private readonly ApplicationDbContext _db;
    private readonly IOptionsSnapshot<RetentionOptions> _options;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<DataRetentionService> _logger;

    public DataRetentionService(
        ApplicationDbContext db, IOptionsSnapshot<RetentionOptions> options, IDateTimeProvider clock, ILogger<DataRetentionService> logger)
    {
        _db = db;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DataRetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var now = _clock.UtcNow;
        var batch = Math.Clamp(options.BatchSize, 100, 5000);
        var deleted = new Dictionary<string, int>();

        async Task Pass(string name, int days, Func<DateTime, Task<int>> run)
        {
            if (RetentionOptions.Effective(days) is not { } keep)
                return;

            try
            {
                deleted[name] = await run(now.AddDays(-keep));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Data retention could not clear {Kind}", name);
            }
        }

        await Pass("WebhookEvents", options.WebhookEventDays, cutoff => Purge(
            _db.WebhookEvents.IgnoreQueryFilters().Where(w => w.ReceivedAt < cutoff && w.ProcessingStatus != WebhookProcessingStatus.Pending), batch, cancellationToken));

        await Pass("RefreshTokens", options.RefreshTokenDays, cutoff => Purge(
            _db.RefreshTokens.IgnoreQueryFilters().Where(t => t.ExpiresAt < cutoff || (t.RevokedAt != null && t.RevokedAt < cutoff)), batch, cancellationToken));

        await Pass("TenantNotifications", options.NotificationDays, cutoff => Purge(
            _db.TenantNotifications.IgnoreQueryFilters().Where(n => n.AcknowledgedAtUtc != null && n.CreatedAt < cutoff), batch, cancellationToken));

        await Pass("PlatformNotifications", options.NotificationDays, cutoff => Purge(
            _db.PlatformNotifications.IgnoreQueryFilters().Where(n => n.AcknowledgedAtUtc != null && n.CreatedAt < cutoff), batch, cancellationToken));

        // The sources and validation failures of a turn go with it (cascade in the database).
        await Pass("AiInteractions", options.AiInteractionDays, cutoff => Purge(
            _db.AiInteractions.IgnoreQueryFilters().Where(i => i.CreatedAt < cutoff), batch, cancellationToken));

        await Pass("AuditLogs", options.AuditLogDays, cutoff => Purge(
            _db.AuditLogs.IgnoreQueryFilters().Where(a => a.PerformedAt < cutoff), batch, cancellationToken));

        var result = new DataRetentionResult(deleted);
        if (result.Total > 0)
            _logger.LogInformation("Data retention removed {Total} rows: {Detail}", result.Total, string.Join(", ", deleted.Where(d => d.Value > 0).Select(d => $"{d.Key} {d.Value}")));

        return result;
    }

    /// <summary>Removes every row the query matches, <paramref name="batch"/> ids at a time. Every table here keys on a Guid <c>Id</c>.</summary>
    private static async Task<int> Purge<T>(IQueryable<T> expired, int batch, CancellationToken cancellationToken) where T : class
    {
        var total = 0;
        while (true)
        {
            var ids = await expired.Select(e => EF.Property<Guid>(e, "Id")).Take(batch).ToListAsync(cancellationToken);
            if (ids.Count == 0)
                return total;

            var removed = await expired.Where(e => ids.Contains(EF.Property<Guid>(e, "Id"))).ExecuteDeleteAsync(cancellationToken);
            total += removed;

            // Nothing was removed (the rows vanished under us), or the last batch was short: done.
            if (removed == 0 || ids.Count < batch)
                return total;
        }
    }
}
