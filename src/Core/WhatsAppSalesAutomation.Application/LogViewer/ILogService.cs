using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.LogViewer;

/// <summary>
/// A queryable view over what <c>_logger.LogWarning</c>/<c>LogError</c>/etc. actually write to disk -
/// built so that reading it does not mean opening a multi-megabyte text file. Read-only: it parses
/// Serilog's existing file output, it does not write or configure logging itself.
/// </summary>
public interface ILogService
{
    Task<PagedResult<LogEntryDto>> GetPagedAsync(LogQueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Calendar dates that have at least one log file, newest first - drives a date picker.</summary>
    IReadOnlyList<DateOnly> GetAvailableDates();

    /// <summary>Distinct, sorted Module values present in the given date's log file (defaults to
    /// today IST, same as GetPagedAsync) - drives a module picker. Free text was never safe to expose
    /// as a dropdown; this is what actually exists to choose from.</summary>
    Task<IReadOnlyList<string>> GetAvailableModulesAsync(DateOnly? date, CancellationToken cancellationToken = default);
}
