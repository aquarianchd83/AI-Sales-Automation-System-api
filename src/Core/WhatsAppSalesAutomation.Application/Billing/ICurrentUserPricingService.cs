namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// The currency to quote platform-wide figures in for whoever is asking - resolved from the signed-in
/// user's own country (ApplicationUser.CountryCode, set on their profile page), falling back to USD.
///
/// Only for figures that belong to the platform rather than to one tenant: the Platform Admin Console's
/// MRR and its cross-tenant spend. A figure about one tenant is quoted in THAT tenant's currency, from the
/// tenant's own country - see PlatformUsageService and PlatformTenantService, which resolve it per row.
/// Mixing the two up would quote a tenant their own numbers in a stranger's currency.
/// </summary>
public interface ICurrentUserPricingService
{
    Task<RegionalPricing> GetAsync(CancellationToken cancellationToken = default);
}
