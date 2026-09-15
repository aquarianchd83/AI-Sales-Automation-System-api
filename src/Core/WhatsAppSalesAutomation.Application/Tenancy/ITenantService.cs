namespace WhatsAppSalesAutomation.Application.Tenancy;

public interface ITenantService
{
    /// <summary>Public, pre-login lookup used by the frontend's branded login page - throws
    /// <see cref="Common.Exceptions.NotFoundException"/> for an unknown slug, never leaks anything
    /// beyond <see cref="TenantPublicDto"/>'s cosmetic fields.</summary>
    Task<TenantPublicDto> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>The calling tenant's own editable profile - requires an ambient tenant (an
    /// authenticated Admin request always has one; see <see cref="Common.Interfaces.ITenantContext"/>).</summary>
    Task<TenantProfileDto> GetProfileForCurrentTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>Self-service business profile change - company and product names, industry,
    /// description, contact details and domain keywords. See <see cref="UpdateTenantBusinessProfileRequest"/>'s
    /// own doc comment.</summary>
    Task<TenantProfileDto> UpdateBusinessProfileForCurrentTenantAsync(UpdateTenantBusinessProfileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Self-service timezone change - see <see cref="UpdateTenantTimezoneRequest"/>'s own
    /// doc comment. A PlatformSuperAdmin can also override this from the Platform Admin Console (see
    /// IPlatformTenantService.UpdateTimezoneAsync) - both paths write the same
    /// <c>Tenant.Timezone</c> column, neither is the sole owner of it.</summary>
    Task<TenantProfileDto> UpdateTimezoneForCurrentTenantAsync(UpdateTenantTimezoneRequest request, CancellationToken cancellationToken = default);

    /// <summary>Self-service country change - see <see cref="UpdateTenantCountryRequest"/>'s own doc
    /// comment. This is what actually fixes a tenant's plan pricing currency (see
    /// Billing.RegionalPricingCatalog.Resolve) when it's showing USD because CountryCode was never set
    /// at signup. A PlatformSuperAdmin can also override this (see
    /// IPlatformTenantService.UpdateCountryAsync) - both paths write the same
    /// <c>Tenant.CountryCode</c> column, neither is the sole owner of it.</summary>
    Task<TenantProfileDto> UpdateCountryForCurrentTenantAsync(UpdateTenantCountryRequest request, CancellationToken cancellationToken = default);
}
