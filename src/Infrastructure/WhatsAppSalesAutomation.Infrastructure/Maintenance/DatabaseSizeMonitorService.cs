using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.Maintenance;

/// <summary>Bound from <c>DatabaseSizeMonitor</c>.</summary>
public class DatabaseSizeMonitorOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Alert once the database uses at least this percentage of its capacity.</summary>
    public double ThresholdPercent { get; set; } = 80;

    /// <summary>The size the database is allowed to grow to, in MB. 0 = work it out: Azure SQL's max size, else the data files' max size
    /// (e.g. SQL Server Express's 10 GB). If neither is limited there is no ceiling to measure against and the check is skipped.</summary>
    public long CapacityMb { get; set; }

    /// <summary>How many of the largest tables to list in the alert.</summary>
    public int TopTables { get; set; } = 10;

    /// <summary>When the threshold is reached, also erase old rows from the tables in <see cref="CleanupTables"/> to free space. Off by default.</summary>
    public bool AutoCleanup { get; set; }

    /// <summary>Days of each selected table the automatic clean-up keeps (minimum 7; the audit trail keeps at least 90, customer messages 180).</summary>
    public int CleanupRetentionDays { get; set; } = 30;

    /// <summary>The tables the product owner has allowed the clean-up to erase from - keys from <c>CleanableTableCatalog</c>. Anything else is ignored.
    /// No initial value on purpose: the configuration binder appends to a pre-filled list instead of replacing it, so the defaults are seeded
    /// into the settings table instead (see AppSettingCatalog).</summary>
    public List<string> CleanupTables { get; set; } = new();
}

public record TableSize(string Name, long Rows, double ReservedMb);

public record CleanupTableResultDto(string Key, string Label, int RowsDeleted);

/// <summary>What clearing one table would free: rows older than the kept days, the table's size, and the estimated space and share it frees.</summary>
public record CleanupPreviewItemDto(
    string Key, long EligibleRows, long TotalRows, double TableMb, double EstimatedFreedMb, double EstimatedFreedPercent, int KeepDays);

/// <summary><paramref name="PercentIsOfQuota"/>: the percentages are shares of the database quota (true) or, with no quota, of the space in use.</summary>
public record CleanupPreviewDto(IReadOnlyList<CleanupPreviewItemDto> Tables, bool PercentIsOfQuota);

/// <summary>One automatic clean-up run: which tables lost rows, and the database size before and after.</summary>
public record CleanupRunDto(
    Guid RunId, DateTime RunAtUtc, int KeepDays, double PercentUsedBefore, double UsedMbBefore, double UsedMbAfter,
    int TotalRows, IReadOnlyList<CleanupTableResultDto> Tables);

public record DatabaseSizeReport(double UsedMb, double? CapacityMb, IReadOnlyList<TableSize> Tables)
{
    public double? PercentUsed => CapacityMb is > 0 ? UsedMb / CapacityMb.Value * 100 : null;
}

/// <summary>
/// Measures how full the database is and which tables are taking the space, and raises a platform alert (in-app + email to every platform
/// operator, via <see cref="IPlatformNotifier"/>) when usage reaches the threshold. The alert key is per UTC day, so a database that stays
/// above the line reminds the operators once a day rather than every run - and a failing mail server cannot cause a flood.
/// Read-only on the database (system views only).
/// </summary>
public class DatabaseSizeMonitorService
{
    private const string TablesSql = @"
SELECT s.name + '.' + t.name AS TableName,
       SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END) AS Rows,
       SUM(ps.reserved_page_count) * 8.0 / 1024.0 AS ReservedMb
FROM sys.dm_db_partition_stats ps
JOIN sys.tables t ON t.object_id = ps.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
GROUP BY s.name, t.name
ORDER BY ReservedMb DESC";

    // Azure SQL reports its ceiling here (NULL elsewhere); otherwise fall back to the data files' own max size
    // (-1 = unlimited; 268435456 pages is the 'unlimited' marker), summed across data files.
    private const string CapacitySql = @"
SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'MaxSizeInBytes') AS float) / 1048576.0 AS AzureMb,
       (SELECT CASE WHEN COUNT(*) > 0 AND MIN(max_size) > 0 AND MAX(max_size) < 268435456
                    THEN SUM(CAST(max_size AS float)) * 8.0 / 1024.0 END
        FROM sys.database_files WHERE type = 0) AS FileMb";

    private readonly ApplicationDbContext _db;
    private readonly IOptionsSnapshot<DatabaseSizeMonitorOptions> _options;
    private readonly IPlatformNotifier _notifier;
    private readonly IPlatformWhatsAppSender _whatsApp;
    private readonly IPlatformNoticeTemplates _templates;
    private readonly IDataRetentionService _retention;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<DatabaseSizeMonitorService> _logger;

    public DatabaseSizeMonitorService(
        ApplicationDbContext db, IOptionsSnapshot<DatabaseSizeMonitorOptions> options, IPlatformNotifier notifier,
        IPlatformWhatsAppSender whatsApp, IPlatformNoticeTemplates templates, IDataRetentionService retention,
        IDateTimeProvider clock, ILogger<DatabaseSizeMonitorService> logger)
    {
        _retention = retention;
        _db = db;
        _options = options;
        _notifier = notifier;
        _whatsApp = whatsApp;
        _templates = templates;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Runs one pass; true when an alert was raised.</summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled)
            return false;

        var report = await MeasureAsync(options, cancellationToken);
        if (report.PercentUsed is not { } percent)
        {
            _logger.LogWarning("Database size monitor: no capacity limit is configured or detectable (set DatabaseSizeMonitor:CapacityMb). Used {UsedMb:F0} MB.", report.UsedMb);
            return false;
        }

        _logger.LogInformation("Database size monitor: {UsedMb:F0} of {CapacityMb:F0} MB used ({Percent:F1}%)", report.UsedMb, report.CapacityMb, percent);
        if (percent < options.ThresholdPercent)
            return false;

        var cleanupNote = options.AutoCleanup ? await CleanUpAsync(options, report, cancellationToken) : null;

        var today = _clock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var severity = percent >= 95 ? PlatformNotificationSeverity.Critical : PlatformNotificationSeverity.Warning;
        var title = $"Database is {percent:F0}% full";
        var raised = await _notifier.NotifyAsync(new PlatformNotificationRequest(
            PlatformNotificationKind.DatabaseCapacity, severity, $"db-capacity-{today}",
            title, BuildBody(report, percent, options, cleanupNote)), cancellationToken);

        // Only when the alert was newly raised, so the WhatsApp message - like the email - goes out once a day, not on every run.
        if (raised)
            await SendWhatsAppAsync(title, report, percent, cancellationToken);
        return raised;
    }

    /// <summary>Frees space by erasing old rows from exactly the tables the product owner selected (<see cref="DatabaseSizeMonitorOptions.CleanupTables"/>,
    /// always within <see cref="CleanableTableCatalog"/>), keeping <see cref="DatabaseSizeMonitorOptions.CleanupRetentionDays"/> days of each. Records
    /// each table that lost rows in <c>DatabaseCleanupLogs</c> for the Database Size screen. Returns a sentence for the alert; never throws.</summary>
    private async Task<string> CleanUpAsync(DatabaseSizeMonitorOptions options, DatabaseSizeReport before, CancellationToken cancellationToken)
    {
        try
        {
            var tables = options.CleanupTables.Where(CleanableTableCatalog.IsKnown).Distinct().ToList();
            if (tables.Count == 0)
                return "Automatic clean-up is on, but no tables are selected for it.";

            var result = await _retention.RunEarlyAsync(options.CleanupRetentionDays, tables, cancellationToken);
            if (result.Total == 0)
                return "Automatic clean-up ran but found nothing old enough to erase in the selected tables - the space is held by other data.";

            var after = await MeasureAsync(options, cancellationToken);

            var runId = Guid.NewGuid();
            var percentBefore = before.PercentUsed ?? 0;
            foreach (var (table, rows) in result.Deleted.Where(d => d.Value > 0))
                _db.DatabaseCleanupLogs.Add(new DatabaseCleanupLog
                {
                    RunId = runId, TableKey = table, RowsDeleted = rows, KeepDays = options.CleanupRetentionDays,
                    PercentUsedBefore = Math.Round(percentBefore, 1), UsedMbBefore = Math.Round(before.UsedMb, 2), UsedMbAfter = Math.Round(after.UsedMb, 2)
                });
            await _db.SaveChangesAsync(cancellationToken);

            string Label(string key) => CleanableTableCatalog.All.FirstOrDefault(t => t.Key == key)?.Label ?? key;
            var detail = string.Join(", ", result.Deleted.Where(d => d.Value > 0).Select(d => $"{Label(d.Key)} {d.Value:N0}"));
            return string.Create(CultureInfo.InvariantCulture,
                $"Automatic clean-up erased {result.Total:N0} rows from the tables you selected ({detail}), keeping {options.CleanupRetentionDays} days. " +
                $"Usage went from {before.UsedMb:F0} MB to {after.UsedMb:F0} MB; SQL Server may release the rest of the freed pages over the next few minutes.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Database size monitor: automatic clean-up failed");
            return "Automatic clean-up failed - see the server log.";
        }
    }

    /// <summary>The platform operator's (product owner's) own "WhatsApp number for urgent alerts" from their profile, through the same approved
    /// "platform setup needed" template the other urgent alerts use. Never throws: the email and in-app alert are already out.</summary>
    private async Task SendWhatsAppAsync(string title, DatabaseSizeReport report, double percent, CancellationToken cancellationToken)
    {
        try
        {
            var phones = await _db.Users.IgnoreQueryFilters()
                .Where(u => u.TenantId == null && u.IsActive && u.PhoneNumber != null && u.PhoneNumber != string.Empty)
                .Select(u => u.PhoneNumber!)
                .ToListAsync(cancellationToken);
            if (phones.Count == 0)
            {
                _logger.LogInformation("Database size alert: no platform operator has a WhatsApp number on their profile");
                return;
            }

            var largest = report.Tables.Count > 0 ? $" Largest table: {report.Tables[0].Name}." : string.Empty;
            var message = string.Create(CultureInfo.InvariantCulture,
                $"{report.UsedMb:F0} of {report.CapacityMb:F0} MB used ({percent:F0}%).{largest} Open Platform > Database Size.");
            var resolution = await _templates.ResolveAsync(TenantNotificationKind.PlatformSetupNeeded, "Platform", title, message, cancellationToken);
            if (resolution.Message is not { } m)
            {
                _logger.LogWarning("Database size alert: WhatsApp not sent - {Reason}", resolution.SkipNote);
                return;
            }

            foreach (var phone in phones.Select(p => new string(p.Where(char.IsDigit).ToArray())).Where(p => p.Length > 0).Distinct())
            {
                var result = await _whatsApp.SendTemplateAsync(phone, m.TemplateName, m.Language, m.Parameters, cancellationToken, m.MediaUrl);
                if (!result.Success)
                    _logger.LogWarning("Database size alert: WhatsApp to {Phone} not sent - {Note}", phone, result.Note);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Database size alert: WhatsApp send failed");
        }
    }

    /// <summary>What an automatic clean-up keeping <paramref name="keepDays"/> days would free right now, per clean-up table and in total. An
    /// estimate: the table's space is shared out by rows, assuming the old rows are about as big as the rest. Deletes nothing.</summary>
    public async Task<CleanupPreviewDto> GetCleanupPreviewAsync(DatabaseSizeMonitorOptions options, int keepDays, CancellationToken cancellationToken = default)
    {
        var report = await MeasureAsync(options, cancellationToken);
        var estimates = await _retention.EstimateEarlyAsync(keepDays, cancellationToken);

        // Percent of the quota when there is one - it matches the "% full" the page shows - else of the space in use.
        var basisMb = report.CapacityMb is > 0 ? report.CapacityMb.Value : report.UsedMb;
        var ofQuota = report.CapacityMb is > 0;

        var items = estimates.Select(e =>
        {
            var size = report.Tables.FirstOrDefault(t => string.Equals(t.Name, e.TableName, StringComparison.OrdinalIgnoreCase));
            var freedMb = size is { Rows: > 0 } ? size.ReservedMb * Math.Min(1.0, (double)e.EligibleRows / size.Rows) : 0;
            return new CleanupPreviewItemDto(
                e.Key, e.EligibleRows, size?.Rows ?? 0, Math.Round(size?.ReservedMb ?? 0, 2), Math.Round(freedMb, 2),
                basisMb > 0 ? Math.Round(freedMb / basisMb * 100, 1) : 0, e.KeepDays);
        }).ToList();

        return new CleanupPreviewDto(items, ofQuota);
    }

    /// <summary>The most recent automatic clean-up runs, newest first, each with the tables that lost rows.</summary>
    public async Task<IReadOnlyList<CleanupRunDto>> GetCleanupHistoryAsync(int runs, CancellationToken cancellationToken = default)
    {
        // One row per table per run (at most 7 a run), so this covers well over the latest `runs`.
        var rows = await _db.DatabaseCleanupLogs.AsNoTracking().OrderByDescending(l => l.CreatedAt).Take(Math.Max(runs, 1) * 10).ToListAsync(cancellationToken);
        string Label(string key) => CleanableTableCatalog.All.FirstOrDefault(t => t.Key == key)?.Label ?? key;

        return rows.GroupBy(l => l.RunId).Take(runs).Select(g =>
        {
            var first = g.First();
            return new CleanupRunDto(
                g.Key, g.Max(l => l.CreatedAt), first.KeepDays, first.PercentUsedBefore, first.UsedMbBefore, first.UsedMbAfter,
                g.Sum(l => l.RowsDeleted),
                g.OrderByDescending(l => l.RowsDeleted).Select(l => new CleanupTableResultDto(l.TableKey, Label(l.TableKey), l.RowsDeleted)).ToList());
        }).ToList();
    }

    /// <summary>Who an alert would reach right now: each active platform operator's alert email (their notification email, else the sign-in one)
    /// and their urgent-alert WhatsApp number - the same lookups the email (PlatformNotifier) and the WhatsApp send use.</summary>
    public async Task<(IReadOnlyList<string> Emails, IReadOnlyList<string> Phones)> GetAlertRecipientsAsync(CancellationToken cancellationToken = default)
    {
        var operators = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == null && u.IsActive)
            .Select(u => new { u.Email, u.NotificationEmail, u.PhoneNumber })
            .ToListAsync(cancellationToken);

        var emails = operators
            .Select(u => string.IsNullOrEmpty(u.NotificationEmail) ? u.Email : u.NotificationEmail)
            .Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!).Distinct().ToList();
        var phones = operators
            .Select(u => u.PhoneNumber)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct().ToList();
        return (emails, phones);
    }

    public async Task<DatabaseSizeReport> MeasureAsync(DatabaseSizeMonitorOptions options, CancellationToken cancellationToken = default)
    {
        var connection = _db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
            await connection.OpenAsync(cancellationToken);
        try
        {
            var tables = new List<TableSize>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = TablesSql;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    tables.Add(new TableSize(reader.GetString(0), Convert.ToInt64(reader.GetValue(1)), Convert.ToDouble(reader.GetValue(2))));
            }

            double? capacity = options.CapacityMb > 0 ? options.CapacityMb : null;
            if (capacity is null)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = CapacitySql;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0)) capacity = reader.GetDouble(0);
                    else if (!reader.IsDBNull(1)) capacity = reader.GetDouble(1);
                }
            }

            return new DatabaseSizeReport(tables.Sum(t => t.ReservedMb), capacity, tables);
        }
        finally
        {
            if (wasClosed)
                await connection.CloseAsync();
        }
    }

    private static string BuildBody(DatabaseSizeReport report, double percent, DatabaseSizeMonitorOptions options, string? cleanupNote)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"The database is using {report.UsedMb:F0} MB of {report.CapacityMb:F0} MB ({percent:F1}%), at or above the {options.ThresholdPercent:F0}% alert level.");
        sb.AppendLine();
        sb.AppendLine("Largest tables:");
        foreach (var t in report.Tables.Take(Math.Max(1, options.TopTables)))
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {t.Name}: {t.ReservedMb:F1} MB ({(report.UsedMb > 0 ? t.ReservedMb / report.UsedMb * 100 : 0):F0}% of used), {t.Rows:N0} rows");
        sb.AppendLine();
        if (cleanupNote is not null)
        {
            sb.AppendLine(cleanupNote);
            sb.AppendLine();
        }
        sb.Append("Free up space by archiving or deleting old data, or raise the database's size limit.");
        return sb.ToString();
    }
}
