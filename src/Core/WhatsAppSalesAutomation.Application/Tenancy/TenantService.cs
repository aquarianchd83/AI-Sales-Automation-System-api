using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Application.Tenancy;

public class TenantService : ITenantService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IValidator<UpdateTenantTimezoneRequest> _updateTimezoneValidator;

    public TenantService(
        IApplicationDbContext context,
        ITenantContext tenantContext,
        IValidator<UpdateTenantTimezoneRequest> updateTimezoneValidator)
    {
        _context = context;
        _tenantContext = tenantContext;
        _updateTimezoneValidator = updateTimezoneValidator;
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

    public async Task<TenantProfileDto> GetProfileForCurrentTenantAsync(CancellationToken cancellationToken = default)
    {
        var tenant = await GetCurrentTenantAsync(cancellationToken);
        return ToDto(tenant);
    }

    public async Task<TenantProfileDto> UpdateTimezoneForCurrentTenantAsync(UpdateTenantTimezoneRequest request, CancellationToken cancellationToken = default)
    {
        await _updateTimezoneValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tenant = await GetCurrentTenantAsync(cancellationToken);
        tenant.Timezone = request.Timezone;
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(tenant);
    }

    private async Task<Tenant> GetCurrentTenantAsync(CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("No tenant in scope for this request.");

        return await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);
    }

    private static TenantProfileDto ToDto(Tenant tenant) =>
        new(string.IsNullOrWhiteSpace(tenant.Timezone) ? TimeZoneCatalog.DefaultId : tenant.Timezone);
}
