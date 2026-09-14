using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformUserSearchService : IPlatformUserSearchService
{
    // A support lookup, not a listing - see IPlatformUserSearchService's own doc comment. Capped
    // rather than paged: if a search term matches more than this many accounts it's too broad to be
    // useful as a "which org is this email in" lookup, and the caller should narrow it instead.
    private const int MaxResults = 25;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IApplicationDbContext _context;

    public PlatformUserSearchService(UserManager<ApplicationUser> userManager, IApplicationDbContext context)
    {
        _userManager = userManager;
        _context = context;
    }

    public async Task<IReadOnlyList<PlatformUserSearchResultDto>> SearchAsync(string? search, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(search))
            return Array.Empty<PlatformUserSearchResultDto>();

        var term = search.Trim();

        var matches = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.Email!.Contains(term) || u.FullName.Contains(term))
            .OrderBy(u => u.Email)
            .Take(MaxResults)
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
            return Array.Empty<PlatformUserSearchResultDto>();

        var tenantIds = matches.Where(u => u.TenantId is not null).Select(u => u.TenantId!.Value).Distinct().ToList();
        var tenantNames = await _context.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var results = new List<PlatformUserSearchResultDto>(matches.Count);
        foreach (var user in matches)
        {
            var roles = await _userManager.GetRolesAsync(user);
            results.Add(new PlatformUserSearchResultDto(
                user.Id, user.Email ?? string.Empty, user.FullName,
                user.TenantId, user.TenantId is { } tenantId ? tenantNames.GetValueOrDefault(tenantId) : null,
                roles as IReadOnlyList<string> ?? roles.ToList(),
                user.IsActive, user.CreatedAt, user.LastLoginAt));
        }

        return results;
    }
}
