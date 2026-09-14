using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.Tenancy;

/// <summary>DI-facing implementation of ITenantTimeZoneProvider - EF Core against the same
/// ApplicationDbContext everything else uses, same "resolve ambient tenant, fall back to the
/// platform default when there isn't one" shape as TenantConfigOverrideProvider.</summary>
public class TenantTimeZoneProvider : ITenantTimeZoneProvider
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _dateTime;

    public TenantTimeZoneProvider(ApplicationDbContext context, ITenantContext tenantContext, IDateTimeProvider dateTime)
    {
        _context = context;
        _tenantContext = tenantContext;
        _dateTime = dateTime;
    }

    public async Task<DateTime> GetLocalNowAsync(CancellationToken cancellationToken = default)
    {
        var timeZoneId = TimeZoneCatalog.DefaultId;

        if (_tenantContext.TenantId is { } tenantId)
        {
            // Ordinary tenant-scoped read - the reflective ITenantOwned filter doesn't apply to
            // Tenant itself (it's the tenant, not something owned by one - see Tenant's own doc
            // comment), so this is a plain lookup by TenantId, same as everywhere else that reads one
            // specific tenant's own row for its own ambient scope.
            var stored = await _context.Tenants
                .Where(t => t.Id == tenantId)
                .Select(t => t.Timezone)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(stored))
                timeZoneId = stored;
        }

        var timeZone = ResolveTimeZone(timeZoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(_dateTime.UtcNow, timeZone);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A stored id the host can't resolve (corrupt data, or a host missing tz data) degrades
            // to the platform default rather than throwing and taking down whatever was scheduling
            // against it - same "corrupt/unusable value behaves as unset" tolerance
            // AppSettingsStore's own TryUnprotect already uses.
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneCatalog.DefaultId);
        }
    }
}
