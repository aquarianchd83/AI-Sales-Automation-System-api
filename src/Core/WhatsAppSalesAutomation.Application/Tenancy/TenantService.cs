using WhatsAppSalesAutomation.Application.Billing;
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
    private readonly IValidator<UpdateTenantCountryRequest> _updateCountryValidator;
    private readonly IValidator<UpdateTenantBusinessProfileRequest> _updateBusinessProfileValidator;

    private readonly ICountryAvailability _countries;

    public TenantService(
        IApplicationDbContext context,
        ITenantContext tenantContext,
        IValidator<UpdateTenantTimezoneRequest> updateTimezoneValidator,
        IValidator<UpdateTenantCountryRequest> updateCountryValidator,
        IValidator<UpdateTenantBusinessProfileRequest> updateBusinessProfileValidator,
        ICountryAvailability countries)
    {
        _countries = countries;
        _context = context;
        _tenantContext = tenantContext;
        _updateTimezoneValidator = updateTimezoneValidator;
        _updateCountryValidator = updateCountryValidator;
        _updateBusinessProfileValidator = updateBusinessProfileValidator;
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
        return TenantProfileDto.From(tenant);
    }

    public async Task<TenantProfileDto> UpdateBusinessProfileForCurrentTenantAsync(UpdateTenantBusinessProfileRequest request, CancellationToken cancellationToken = default)
    {
        await _updateBusinessProfileValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tenant = await GetCurrentTenantAsync(cancellationToken);
        tenant.Name = request.CompanyName.Trim();
        TenantBusinessDetails.ApplyTo(tenant, request);
        await _context.SaveChangesAsync(cancellationToken);

        return TenantProfileDto.From(tenant);
    }

    public async Task<TenantProfileDto> UpdateTimezoneForCurrentTenantAsync(UpdateTenantTimezoneRequest request, CancellationToken cancellationToken = default)
    {
        await _updateTimezoneValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tenant = await GetCurrentTenantAsync(cancellationToken);
        tenant.Timezone = request.Timezone;
        await _context.SaveChangesAsync(cancellationToken);

        return TenantProfileDto.From(tenant);
    }

    public async Task<TenantProfileDto> UpdateCountryForCurrentTenantAsync(UpdateTenantCountryRequest request, CancellationToken cancellationToken = default)
    {
        await _updateCountryValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tenant = await GetCurrentTenantAsync(cancellationToken);
        await _countries.EnsureAllowedAsync(request.CountryCode, tenant.CountryCode, cancellationToken);
        tenant.CountryCode = request.CountryCode;
        await _context.SaveChangesAsync(cancellationToken);

        return TenantProfileDto.From(tenant);
    }

    private async Task<Tenant> GetCurrentTenantAsync(CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("No tenant in scope for this request.");

        return await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);
    }
}
