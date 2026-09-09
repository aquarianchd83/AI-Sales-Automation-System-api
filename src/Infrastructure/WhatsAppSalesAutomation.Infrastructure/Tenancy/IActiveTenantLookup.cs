using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Tenancy;

/// <summary>
/// Every tenant a recurring background job should act on behalf of - see
/// <see cref="BackgroundJobs.TenantJobRunner"/>'s own doc comment for how the per-tenant job loop uses
/// this. Excludes <see cref="TenantStatus.Suspended"/>/<see cref="TenantStatus.Cancelled"/> tenants: a
/// suspended/cancelled tenant should not keep sending campaigns or syncing templates just because a
/// background job doesn't know to stop - <c>Trial</c> and <c>Active</c> both count as active for this
/// purpose (a trial tenant's campaigns should still run).
/// </summary>
public interface IActiveTenantLookup
{
    Task<IReadOnlyList<Guid>> GetActiveTenantIdsAsync(CancellationToken cancellationToken = default);
}

public class ActiveTenantLookup : IActiveTenantLookup
{
    private readonly IApplicationDbContext _context;

    public ActiveTenantLookup(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Guid>> GetActiveTenantIdsAsync(CancellationToken cancellationToken = default)
    {
        // Tenant itself carries no query filter (it is the root, not something ITenantOwned - see its
        // own doc comment), so this is a plain, ungated query regardless of which tenant (if any) is
        // ambient when this runs - exactly what a job's very first, tenant-discovering query needs.
        return await _context.Tenants
            .Where(t => t.Status != TenantStatus.Suspended && t.Status != TenantStatus.Cancelled)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }
}
