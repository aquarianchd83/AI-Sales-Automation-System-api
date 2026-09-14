using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.LogViewer;

public partial class LogService : ILogService
{
    // Matches the start of one entry written with this app's current output template:
    // "2026-09-03 10:49:47.697 +05:30 [WRN] [My.Namespace.MyClass::MyMethod] [t:<tenant guid>] message text".
    // The "[Namespace::Method]" segment is optional so lines written before Serilog.Enrichers.CallerInfo
    // was added (the old "2026-09-03 ... [WRN] message text" shape, still sitting in older log files)
    // keep parsing correctly too - they just come back with a null Module/Method. The "[t:...]" segment
    // is optional for the same reason (lines from before tenant tagging), and is written as an empty
    // "[t:]" for a line with no tenant. Any line that matches neither shape - a wrapped exception stack
    // trace, or a multi-line message like the Hangfire startup banner - is a continuation of the
    // previous entry, not a new one.
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<lvl>\w{3})\] (?:\[(?<ns>[^\]]*)::(?<method>[^\]]*)\] )?(?:\[t:(?<tenant>[^\]]*)\] )?(?<msg>.*)$")]
    private static partial Regex EntryStartPattern();

    // Serilog's default text template writes the 3-letter code; accepting the full name too on the
    // way in means "Warning" and "WRN" both work as a filter value without the caller needing to know
    // which form the file actually uses.
    private static readonly IReadOnlyDictionary<string, string> LevelCodeToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["VRB"] = "Verbose",
        ["DBG"] = "Debug",
        ["INF"] = "Information",
        ["WRN"] = "Warning",
        ["ERR"] = "Error",
        ["FTL"] = "Fatal"
    };

    private readonly ILogFileReaderService _reader;
    private readonly IDateTimeProvider _dateTime;
    private readonly IApplicationDbContext _context;

    public LogService(ILogFileReaderService reader, IDateTimeProvider dateTime, IApplicationDbContext context)
    {
        _reader = reader;
        _dateTime = dateTime;
        _context = context;
    }

    public IReadOnlyList<DateOnly> GetAvailableDates() => _reader.GetAvailableDates();

    public async Task<IReadOnlyList<string>> GetAvailableModulesAsync(DateOnly? date, CancellationToken cancellationToken = default)
    {
        var resolvedDate = date ?? DateOnly.FromDateTime(_dateTime.IstNow);
        var lines = await _reader.ReadLinesAsync(resolvedDate, cancellationToken);

        return Parse(lines)
            .Where(e => e.Module is not null)
            .Select(e => e.Module!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<PagedResult<LogEntryDto>> GetPagedAsync(LogQueryRequest request, CancellationToken cancellationToken = default)
    {
        // Normalized to full names up front, so "WRN" and "Warning" in the same request collapse into one
        // entry and an unknown level fails the whole request rather than silently matching nothing.
        HashSet<string>? levelFilter = null;
        var requestedLevels = (request.Level ?? Array.Empty<string>())
            .SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        if (requestedLevels.Count > 0)
        {
            levelFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var level in requestedLevels)
            {
                levelFilter.Add(NormalizeLevelName(level)
                    ?? throw Invalid(nameof(request.Level), $"Level must be one of: {string.Join(", ", LevelCodeToName.Values)}."));
            }
        }

        var date = request.Date ?? DateOnly.FromDateTime(_dateTime.IstNow);
        var lines = await _reader.ReadLinesAsync(date, cancellationToken);
        var entries = Parse(lines);

        if (levelFilter is not null)
            entries = entries.Where(e => levelFilter.Contains(e.Level)).ToList();

        if (request.TenantId is { } tenantId)
            entries = entries.Where(e => e.TenantId == tenantId).ToList();

        if (!string.IsNullOrWhiteSpace(request.Module))
        {
            var module = request.Module.Trim();
            entries = entries.Where(e => e.Module is not null && e.Module.Contains(module, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.Method))
        {
            var method = request.Method.Trim();
            entries = entries.Where(e => e.Method is not null && e.Method.Contains(method, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            entries = entries.Where(e => e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Newest first, matching every other paged listing in this codebase (conversations, messages).
        entries = entries.OrderByDescending(e => e.Timestamp).ToList();

        var totalCount = entries.Count;
        var page = entries
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToList();

        page = await WithTenantNamesAsync(page, cancellationToken);

        return new PagedResult<LogEntryDto>(page, totalCount, request.Page, request.PageSize);
    }

    /// <summary>The file only carries the tenant id, so names are looked up for the one page being
    /// returned rather than the whole day. IgnoreQueryFilters so a since-deleted tenant still gets its
    /// name instead of showing as a bare id.</summary>
    private async Task<List<LogEntryDto>> WithTenantNamesAsync(List<LogEntryDto> page, CancellationToken cancellationToken)
    {
        var tenantIds = page.Where(e => e.TenantId is not null).Select(e => e.TenantId!.Value).Distinct().ToList();
        if (tenantIds.Count == 0)
            return page;

        var names = await _context.Tenants
            .IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        return page
            .Select(e => e.TenantId is { } id && names.TryGetValue(id, out var name) ? e with { TenantName = name } : e)
            .ToList();
    }

    /// <summary>Groups raw lines into entries: a line matching <see cref="EntryStartPattern"/> starts a
    /// new one, everything after it up to the next match is folded into that entry's message.</summary>
    private static List<LogEntryDto> Parse(IReadOnlyList<string> lines)
    {
        var entries = new List<LogEntryDto>();
        DateTimeOffset? timestamp = null;
        string? level = null;
        string? module = null;
        string? method = null;
        Guid? tenantId = null;
        List<string>? messageLines = null;

        void Flush()
        {
            if (timestamp is null || level is null || messageLines is null)
                return;

            entries.Add(new LogEntryDto(timestamp.Value, level, module, method, string.Join(Environment.NewLine, messageLines), tenantId));
        }

        foreach (var line in lines)
        {
            var match = EntryStartPattern().Match(line);
            if (match.Success)
            {
                Flush();

                timestamp = DateTimeOffset.TryParseExact(
                    match.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
                level = LevelCodeToName.TryGetValue(match.Groups["lvl"].Value, out var name) ? name : match.Groups["lvl"].Value;
                // Empty rather than absent when the "[Namespace::Method]" segment matched but came
                // back blank (the enricher found no frame in our own assemblies) - normalize both that
                // case and "the segment wasn't there at all" (older log lines) to null.
                module = string.IsNullOrEmpty(match.Groups["ns"].Value) ? null : match.Groups["ns"].Value;
                method = string.IsNullOrEmpty(match.Groups["method"].Value) ? null : match.Groups["method"].Value;
                tenantId = Guid.TryParse(match.Groups["tenant"].Value, out var parsedTenantId) ? parsedTenantId : null;
                messageLines = new List<string> { match.Groups["msg"].Value };
            }
            else
            {
                // A continuation line before any entry has started (should not happen in practice,
                // but a truncated/corrupt file is possible) - nothing to attach it to, so drop it.
                messageLines?.Add(line);
            }
        }

        Flush();
        return entries;
    }

    private static string? NormalizeLevelName(string level)
    {
        if (LevelCodeToName.TryGetValue(level, out var byCode))
            return byCode;

        return LevelCodeToName.Values.FirstOrDefault(name => string.Equals(name, level, StringComparison.OrdinalIgnoreCase));
    }

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
