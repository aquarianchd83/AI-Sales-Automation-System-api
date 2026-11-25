using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Simulator;

/// <summary>Where every simulator reads its dummy data from: the SimulatorDummyRecords table, edited on the platform admin's
/// Simulator dummy data page.</summary>
public interface ISimulatorDataProvider
{
    Task<SimulatorDataSnapshot> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the cached copy so the next simulator call reads the edit that was just saved.</summary>
    void Invalidate();
}

/// <summary>Reads the table and keeps it for a short while, so a busy simulator does not query it for every message.</summary>
public sealed class SimulatorDataProvider : ISimulatorDataProvider
{
    private const string CacheKey = "simulator-dummy-data";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SimulatorDataProvider> _logger;

    public SimulatorDataProvider(IApplicationDbContext context, IMemoryCache cache, ILogger<SimulatorDataProvider> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<SimulatorDataSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out SimulatorDataSnapshot? cached) && cached is not null)
            return cached;

        try
        {
            var rows = await _context.SimulatorDummyRecords.AsNoTracking().Where(r => r.IsActive).ToListAsync(cancellationToken);
            var snapshot = new SimulatorDataSnapshot(rows);
            _cache.Set(CacheKey, snapshot, CacheFor);
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A simulator must not take a whole flow down because its test data could not be read (e.g. the migration has not run yet).
            _logger.LogWarning(ex, "Simulator dummy data could not be read - using the built-in starter data for this call.");
            return SimulatorDataSnapshot.Defaults;
        }
    }

    public void Invalidate() => _cache.Remove(CacheKey);
}
