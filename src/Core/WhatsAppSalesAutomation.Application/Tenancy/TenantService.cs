using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Application.Tenancy;

public class TenantService : ITenantService
{
    private readonly IApplicationDbContext _context;

    public TenantService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<TenantPublicDto> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        var tenant = await _context.Tenants
            .Where(t => t.Slug == normalized)
            .Select(t => new { t.Name, t.Slug })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), slug);

        return new TenantPublicDto(tenant.Name, tenant.Slug);
    }
}
