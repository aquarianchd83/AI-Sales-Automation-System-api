using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
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
}

public record TableSize(string Name, long Rows, double ReservedMb);

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
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<DatabaseSizeMonitorService> _logger;

    public DatabaseSizeMonitorService(
        ApplicationDbContext db, IOptionsSnapshot<DatabaseSizeMonitorOptions> options, IPlatformNotifier notifier,
        IDateTimeProvider clock, ILogger<DatabaseSizeMonitorService> logger)
    {
        _db = db;
        _options = options;
        _notifier = notifier;
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

        var today = _clock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var severity = percent >= 95 ? PlatformNotificationSeverity.Critical : PlatformNotificationSeverity.Warning;
        return await _notifier.NotifyAsync(new PlatformNotificationRequest(
            PlatformNotificationKind.DatabaseCapacity, severity, $"db-capacity-{today}",
            $"Database is {percent:F0}% full",
            BuildBody(report, percent, options)), cancellationToken);
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

    private static string BuildBody(DatabaseSizeReport report, double percent, DatabaseSizeMonitorOptions options)
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
        sb.Append("Free up space by archiving or deleting old data, or raise the database's size limit.");
        return sb.ToString();
    }
}
