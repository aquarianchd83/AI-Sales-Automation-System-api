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
///
/// Each table is one <see cref="Rule"/> holding its "what is expired" query once; deleting and counting (for the clean-up preview) both
/// run that same query, so the preview can never promise rows the clean-up would not remove.
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

    /// <param name="ConfiguredDays">The regular retention for the table; 0 = off.</param>
    /// <param name="EarlyOnly">Never part of the regular daily pass - only an early clean-up the owner selected may touch it.</param>
    private sealed record Rule(
        string Key, int ConfiguredDays, string TableName, bool EarlyOnly,
        Func<DateTime, int, CancellationToken, Task<int>> Purge, Func<DateTime, CancellationToken, Task<int>> Count);

    public Task<DataRetentionResult> RunAsync(CancellationToken cancellationToken = default) => RunCoreAsync(null, null, cancellationToken);

    public Task<DataRetentionResult> RunEarlyAsync(int keepDays, IReadOnlyCollection<string> tables, CancellationToken cancellationToken = default)
        => RunCoreAsync(Math.Max(keepDays, RetentionOptions.MinimumDays), tables.ToHashSet(), cancellationToken);

    public async Task<IReadOnlyList<CleanupEstimate>> EstimateEarlyAsync(int keepDays, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var now = _clock.UtcNow;
        var early = Math.Max(keepDays, RetentionOptions.MinimumDays);
        var estimates = new List<CleanupEstimate>();

        foreach (var rule in Rules(options))
        {
            var keep = KeepDays(rule, early);
            long rows = 0;
            try
            {
                rows = await rule.Count(now.AddDays(-keep), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Clean-up preview could not count {Kind}", rule.Key);
            }
            estimates.Add(new CleanupEstimate(rule.Key, rule.TableName, rows, keep));
        }

        return estimates;
    }

    private static int KeepDays(Rule rule, int earlyKeepDays)
    {
        // An early clean-up only ever shortens retention - and an off (0) setting is not "forever" here, it is capped too.
        var floor = CleanableTableCatalog.All.FirstOrDefault(t => t.Key == rule.Key)?.FloorDays ?? 0;
        var cap = Math.Max(earlyKeepDays, floor);
        return RetentionOptions.Effective(rule.ConfiguredDays) is { } configured ? Math.Min(configured, cap) : cap;
    }

    private async Task<DataRetentionResult> RunCoreAsync(int? earlyKeepDays, HashSet<string>? selected, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var now = _clock.UtcNow;
        var batch = Math.Clamp(options.BatchSize, 100, 5000);
        var deleted = new Dictionary<string, int>();

        foreach (var rule in Rules(options))
        {
            // The regular pass never touches an early-only table; an early clean-up only touches the tables the owner selected.
            if (earlyKeepDays is null && rule.EarlyOnly)
                continue;
            if (selected is not null && !selected.Contains(rule.Key))
                continue;

            int keep;
            if (earlyKeepDays is { } early)
                keep = KeepDays(rule, early);
            else if (RetentionOptions.Effective(rule.ConfiguredDays) is { } configured)
                keep = configured;
            else
                continue;

            try
            {
                deleted[rule.Key] = await rule.Purge(now.AddDays(-keep), batch, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Data retention could not clear {Kind}", rule.Key);
            }
        }

        var result = new DataRetentionResult(deleted);
        if (result.Total > 0)
            _logger.LogInformation("Data retention removed {Total} rows: {Detail}", result.Total, string.Join(", ", deleted.Where(d => d.Value > 0).Select(d => $"{d.Key} {d.Value}")));

        return result;
    }

    private List<Rule> Rules(RetentionOptions o) => new()
    {
        Make(CleanableTableCatalog.WebhookEvents, o.WebhookEventDays, false, cutoff =>
            _db.WebhookEvents.IgnoreQueryFilters().Where(w => w.ReceivedAt < cutoff && w.ProcessingStatus != WebhookProcessingStatus.Pending)),

        Make(CleanableTableCatalog.RefreshTokens, o.RefreshTokenDays, false, cutoff =>
            _db.RefreshTokens.IgnoreQueryFilters().Where(t => t.ExpiresAt < cutoff || (t.RevokedAt != null && t.RevokedAt < cutoff))),

        Make(CleanableTableCatalog.TenantNotifications, o.NotificationDays, false, cutoff =>
            _db.TenantNotifications.IgnoreQueryFilters().Where(n => n.AcknowledgedAtUtc != null && n.CreatedAt < cutoff)),

        Make(CleanableTableCatalog.PlatformNotifications, o.NotificationDays, false, cutoff =>
            _db.PlatformNotifications.IgnoreQueryFilters().Where(n => n.AcknowledgedAtUtc != null && n.CreatedAt < cutoff)),

        // The sources and validation failures of a turn go with it (cascade in the database).
        Make(CleanableTableCatalog.AiInteractions, o.AiInteractionDays, false, cutoff =>
            _db.AiInteractions.IgnoreQueryFilters().Where(i => i.CreatedAt < cutoff)),

        Make(CleanableTableCatalog.AuditLogs, o.AuditLogDays, false, cutoff =>
            _db.AuditLogs.IgnoreQueryFilters().Where(a => a.PerformedAt < cutoff)),

        // Customer messages are business data: there is no regular retention for them (0 = off), so they only ever go in an early clean-up that
        // the owner explicitly selected, and never while a message is still waiting to be sent.
        Make(CleanableTableCatalog.Messages, 0, true, cutoff =>
            _db.Messages.IgnoreQueryFilters().Where(m => m.CreatedAt < cutoff && m.Status != MessageStatus.Queued)),
    };

    private Rule Make<T>(string key, int configuredDays, bool earlyOnly, Func<DateTime, IQueryable<T>> expired) where T : class
    {
        var table = _db.Model.FindEntityType(typeof(T));
        var name = $"{table?.GetSchema() ?? "dbo"}.{table?.GetTableName() ?? typeof(T).Name}";
        return new Rule(key, configuredDays, name, earlyOnly,
            (cutoff, batch, ct) => Purge(expired(cutoff), batch, ct),
            (cutoff, ct) => expired(cutoff).CountAsync(ct));
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
