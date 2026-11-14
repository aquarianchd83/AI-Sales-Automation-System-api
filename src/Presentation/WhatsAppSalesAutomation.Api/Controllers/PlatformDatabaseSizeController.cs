using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Infrastructure.Maintenance;

namespace WhatsAppSalesAutomation.Api.Controllers;

public record DatabaseTableSizeDto(string Name, long Rows, double SizeMb, double PercentOfUsed);

/// <summary>A table the product owner can allow the automatic clean-up to erase from. <paramref name="Selected"/> is whether it is allowed now.</summary>
public record CleanableTableDto(string Key, string Label, string Description, bool IsBusinessData, int FloorDays, bool Selected);

/// <summary><paramref name="CapacityMb"/> and <paramref name="PercentUsed"/> are null when no capacity limit is configured or detectable.
/// <paramref name="ConfiguredCapacityMb"/> is the quota the operator saved (0 = none, detect from the server); <paramref name="CapacityMb"/> is what is in effect.</summary>
public record DatabaseSizeDto(
    double UsedMb, double? CapacityMb, double? PercentUsed, double ThresholdPercent, bool OverThreshold, bool MonitorEnabled,
    DateTime MeasuredAtUtc, IReadOnlyList<DatabaseTableSizeDto> Tables, long ConfiguredCapacityMb,
    IReadOnlyList<string> AlertEmails, IReadOnlyList<string> AlertPhones, bool AutoCleanup, int CleanupRetentionDays,
    IReadOnlyList<CleanableTableDto> CleanableTables, IReadOnlyList<CleanupRunDto> Cleanups);

/// <summary>
/// Platform Admin Console's Database Size screen - PlatformSuperAdmin-only. Live read of the same measurement the daily
/// <c>database-size-monitor</c> job alerts on, plus the tables the owner lets the automatic clean-up erase from and what it has erased so far.
/// Read-only: the quota, alert level and table selection are saved through the platform settings API (category DatabaseSizeMonitor).
/// </summary>
[ApiController]
[Route("api/v1/platform/database-size")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformDatabaseSizeController : ControllerBase
{
    private const int RecentCleanups = 10;

    private readonly DatabaseSizeMonitorService _monitor;
    private readonly IOptionsSnapshot<DatabaseSizeMonitorOptions> _options;

    public PlatformDatabaseSizeController(DatabaseSizeMonitorService monitor, IOptionsSnapshot<DatabaseSizeMonitorOptions> options)
    {
        _monitor = monitor;
        _options = options;
    }

    /// <summary>How much space the automatic clean-up would free right now, per table, if it kept <paramref name="keepDays"/> days (default: the saved
    /// number). Counts only - nothing is deleted. Takes the days as a parameter so the screen can preview a number before it is saved.</summary>
    [HttpGet("cleanup-preview")]
    public async Task<ActionResult<CleanupPreviewDto>> GetCleanupPreview([FromQuery] int? keepDays, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        return Ok(await _monitor.GetCleanupPreviewAsync(options, Math.Clamp(keepDays ?? options.CleanupRetentionDays, 7, 3650), cancellationToken));
    }

    /// <summary>Database usage against capacity plus every table, largest first, and the clean-up selection and history.</summary>
    [HttpGet]
    public async Task<ActionResult<DatabaseSizeDto>> Get(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var report = await _monitor.MeasureAsync(options, cancellationToken);
        var recipients = await _monitor.GetAlertRecipientsAsync(cancellationToken);
        var cleanups = await _monitor.GetCleanupHistoryAsync(RecentCleanups, cancellationToken);
        var percent = report.PercentUsed;
        var tables = report.Tables
            .Select(t => new DatabaseTableSizeDto(t.Name, t.Rows, Math.Round(t.ReservedMb, 2),
                report.UsedMb > 0 ? Math.Round(t.ReservedMb / report.UsedMb * 100, 1) : 0))
            .ToList();
        var selected = options.CleanupTables.ToHashSet();
        var cleanable = CleanableTableCatalog.All
            .Select(t => new CleanableTableDto(t.Key, t.Label, t.Description, t.IsBusinessData, t.FloorDays, selected.Contains(t.Key)))
            .ToList();

        return Ok(new DatabaseSizeDto(
            Math.Round(report.UsedMb, 2),
            report.CapacityMb is { } c ? Math.Round(c, 2) : null,
            percent is { } p ? Math.Round(p, 1) : null,
            options.ThresholdPercent,
            percent >= options.ThresholdPercent,
            options.Enabled,
            DateTime.UtcNow,
            tables,
            options.CapacityMb,
            recipients.Emails,
            recipients.Phones,
            options.AutoCleanup,
            options.CleanupRetentionDays,
            cleanable,
            cleanups));
    }
}
