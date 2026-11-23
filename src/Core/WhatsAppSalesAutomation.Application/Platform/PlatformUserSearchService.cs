using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Users;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformUserSearchService : IPlatformUserSearchService
{
    // A support lookup, not a listing - see IPlatformUserSearchService's own doc comment. Capped
    // rather than paged: if a search term matches more than this many accounts it's too broad to be
    // useful as a "which org is this email in" lookup, and the caller should narrow it instead.
    private const int MaxResults = 25;
    private const int MaxTenantResults = 200;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IApplicationDbContext _context;

    public PlatformUserSearchService(UserManager<ApplicationUser> userManager, IApplicationDbContext context)
    {
        _userManager = userManager;
        _context = context;
    }

    public async Task<IReadOnlyList<PlatformUserSearchResultDto>> SearchAsync(string? search, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(search) && tenantId is null)
            return Array.Empty<PlatformUserSearchResultDto>();

        var query = _userManager.Users.IgnoreQueryFilters().AsQueryable();
        if (tenantId is { } scopedTenantId)
            query = query.Where(u => u.TenantId == scopedTenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(u => u.Email!.Contains(term) || u.FullName.Contains(term));
        }

        // One tenant's own user list is bounded by its plan, so it gets a larger cap than a
        // cross-tenant lookup.
        var matches = await query
            .OrderBy(u => u.Email)
            .Take(tenantId is null ? MaxResults : MaxTenantResults)
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
            return Array.Empty<PlatformUserSearchResultDto>();

        var tenantIds = matches.Where(u => u.TenantId is not null).Select(u => u.TenantId!.Value).Distinct().ToList();
        var tenantNames = await _context.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        // Roles for every match in one query, not a GetRolesAsync round trip per user.
        var rolesByUser = await _context.RolesByUserAsync(matches.Select(u => u.Id), cancellationToken);

        return matches.Select(user => new PlatformUserSearchResultDto(
                user.Id, user.Email ?? string.Empty, user.FullName,
                user.TenantId, user.TenantId is { } userTenantId ? tenantNames.GetValueOrDefault(userTenantId) : null,
                rolesByUser.For(user.Id),
                user.IsActive, user.CreatedAt, user.LastLoginAt))
            .ToList();
    }
}
