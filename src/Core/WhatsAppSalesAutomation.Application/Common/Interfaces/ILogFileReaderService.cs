namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Raw access to Serilog's own file output (see the "Serilog" config section - this only reads what
/// it already writes, it does not configure the writer itself). Implemented in Infrastructure so
/// <c>LogService</c> stays free of file-system/hosting concerns, the same split Media uses between
/// <c>MediaService</c> and <see cref="IMediaStorageService"/>.
/// </summary>
public interface ILogFileReaderService
{
    /// <summary>Calendar dates that have at least one log file on disk, newest first - drives a date picker.</summary>
    IReadOnlyList<DateOnly> GetAvailableDates();

    /// <summary>
    /// Every line written for that date, oldest first. A day can roll into more than one physical file
    /// when the process restarts (e.g. log-20260903.txt, log-20260903_001.txt), so this concatenates
    /// them in the order they were created rather than assuming one file per day.
    /// </summary>
    Task<IReadOnlyList<string>> ReadLinesAsync(DateOnly date, CancellationToken cancellationToken = default);
}
