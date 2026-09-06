using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Logging;

/// <summary>
/// Reads Serilog's own rolling file output from local disk. Implements <see cref="ILogFileReaderService"/>
/// so <c>LogService</c> does not touch the file system directly - same split as
/// <c>LocalFileMediaStorageService</c>/<c>IMediaStorageService</c>.
/// </summary>
public class LocalLogFileReaderService : ILogFileReaderService
{
    private readonly string _directory;
    private readonly string _filePrefix;

    public LocalLogFileReaderService(IWebHostEnvironment environment, IOptions<LocalLogFileReaderSettings> settings)
    {
        var s = settings.Value;
        _filePrefix = s.FilePrefix;
        _directory = Path.IsPathRooted(s.Directory) ? s.Directory : Path.Combine(environment.ContentRootPath, s.Directory);
    }

    public IReadOnlyList<DateOnly> GetAvailableDates()
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<DateOnly>();

        return Directory.EnumerateFiles(_directory, $"{_filePrefix}*.txt")
            .Select(TryExtractDate)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<string>();

        var stamp = date.ToString("yyyyMMdd");

        // A day can roll into more than one physical file when the process restarts
        // (log-20260903.txt, log-20260903_001.txt, ...) - the "_NNN" suffix is zero-padded, so an
        // ordinal name sort already puts them back in write order.
        var files = new DirectoryInfo(_directory)
            .GetFiles($"{_filePrefix}{stamp}*.txt")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        var lines = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // FileShare.ReadWrite: today's file is held open for writing by Serilog's own File sink for
            // as long as the process runs - without ReadWrite share this would throw on every attempt
            // to read the current day's log.
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                lines.Add(line);
        }

        return lines;
    }

    private DateOnly? TryExtractDate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(_filePrefix, StringComparison.Ordinal))
            return null;

        var datePart = name[_filePrefix.Length..].Split('_')[0];
        return DateOnly.TryParseExact(datePart, "yyyyMMdd", out var parsed) ? parsed : null;
    }
}
