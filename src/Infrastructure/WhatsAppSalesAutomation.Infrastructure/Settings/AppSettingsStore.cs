using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.Settings;

/// <summary>DI-facing implementation of IAppSettingsStore - EF Core against the same
/// ApplicationDbContext everything else uses (unlike AppSettingsConfigurationProvider, which reads
/// the same table via raw ADO.NET because it runs before the DI container exists).</summary>
public class AppSettingsStore : IAppSettingsStore
{
    private readonly ApplicationDbContext _context;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IAppSettingsReloader _reloader;
    private readonly IDateTimeProvider _dateTime;

    public AppSettingsStore(
        ApplicationDbContext context,
        IDataProtectionProvider dataProtectionProvider,
        IAppSettingsReloader reloader,
        IDateTimeProvider dateTime)
    {
        _context = context;
        _dataProtectionProvider = dataProtectionProvider;
        _reloader = reloader;
        _dateTime = dateTime;
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _context.AppSettings.AsNoTracking().ToListAsync(cancellationToken);
        var protector = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider);

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            result[row.Key] = row.IsSecret && row.Value is not null ? TryUnprotect(protector, row.Value) : row.Value;

        return result;
    }

    public async Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        if (values.Count == 0)
            return;

        var protector = AppSettingsSecretProtection.CreateProtector(_dataProtectionProvider);
        var definitionsByKey = AppSettingCatalog.All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

        var keys = values.Keys.ToList();
        var existing = (await _context.AppSettings.Where(s => keys.Contains(s.Key)).ToListAsync(cancellationToken))
            .ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rawValue) in values)
        {
            // Defensive only - SettingsService.UpdateCategoryAsync already rejects keys outside the
            // target category's catalog entries before this is ever called.
            if (!definitionsByKey.TryGetValue(key, out var definition))
                continue;

            if (!existing.TryGetValue(key, out var row))
            {
                row = new AppSetting { Key = key };
                _context.AppSettings.Add(row);
            }

            row.IsSecret = definition.IsSecret;
            row.Value = rawValue is null ? null : definition.IsSecret ? protector.Protect(rawValue) : rawValue;
            row.UpdatedAtUtc = _dateTime.UtcNow;
            row.UpdatedByUserId = updatedByUserId;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Live no-restart update: every IOptionsSnapshot<T>/IOptionsMonitor<T> consumer re-resolves
        // from IConfiguration on its next scope/change - see AppSettingsConfigurationProvider.Reload().
        _reloader.Reload();
    }

    private static string? TryUnprotect(IDataProtector protector, string value)
    {
        try
        {
            return protector.Unprotect(value);
        }
        catch
        {
            return null;
        }
    }
}
