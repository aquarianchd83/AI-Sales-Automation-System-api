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

        // Resolution (and its fall back to the platform default for an unusable id) lives on the catalog, so
        // this and TenantMonth can't drift apart on what a stored timezone means.
        var timeZone = TimeZoneCatalog.Resolve(timeZoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(_dateTime.UtcNow, timeZone);
    }
}
