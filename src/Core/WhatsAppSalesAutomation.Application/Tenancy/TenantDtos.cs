using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>
/// The only fields safe to expose pre-login (see <see cref="ITenantService.GetBySlugAsync"/>) -
/// cosmetic branding for the frontend's login page, nothing that reveals tenant-internal data.
/// </summary>
public record TenantPublicDto(string Name, string Slug);

/// <summary>The calling tenant's own editable profile (see
/// ITenantService.GetProfileForCurrentTenantAsync) - its business details plus Timezone/CountryCode.
/// CompanyName is Tenant.Name, the name chosen at signup. Timezone is always the effective value,
/// defaulting to TimeZoneCatalog.DefaultId the same way ITenantTimeZoneProvider does, so it never returns
/// null/empty even before the tenant has ever set one explicitly. CountryCode has no such universal
/// default (see AuthService.SignUpAsync's own comment) - null here means "never set, billing quotes in
/// USD" (see RegionalPricingCatalog.Resolve), not an error. DomainKeywords is never null.</summary>
public record TenantProfileDto(
    string CompanyName,
    string? ProductName,
    string? Industry,
    string? BusinessDescription,
    string? WebsiteUrl,
    string? SupportEmail,
    string? SupportPhone,
    IReadOnlyList<string> DomainKeywords,
    string Timezone,
    string? CountryCode)
{
    /// <summary>Shared by the tenant's self-service endpoints and the Platform Admin Console's
    /// timezone/country overrides, so every path returns the same effective values.</summary>
    public static TenantProfileDto From(Tenant tenant) => new(
        tenant.Name,
        tenant.ProductName,
        tenant.Industry,
        tenant.BusinessDescription,
        tenant.WebsiteUrl,
        tenant.SupportEmail,
        tenant.SupportPhone,
        tenant.DomainKeywords.ToList(),
        string.IsNullOrWhiteSpace(tenant.Timezone) ? TimeZoneCatalog.DefaultId : tenant.Timezone,
        tenant.CountryCode);
}

/// <summary>Body of PUT the tenant's own business profile - replaces every business field at once, so
/// a blank optional field clears it. CompanyName renames the tenant (Tenant.Name) but never changes its
/// Slug. DomainKeywords are trimmed and de-duplicated case-insensitively, and blanks dropped. Timezone
/// and Country keep their own endpoints: each is checked against a catalog, and the Platform Admin
/// Console overrides them independently.</summary>
public record UpdateTenantBusinessProfileRequest(
    string CompanyName,
    string? ProductName,
    string? Industry,
    string? BusinessDescription,
    string? WebsiteUrl,
    string? SupportEmail,
    string? SupportPhone,
    IReadOnlyList<string>? DomainKeywords) : ITenantBusinessDetails;

/// <summary>Body of PUT the tenant's own timezone - see TenantProfileDto's own doc comment for why
/// this is never blank on the way out; on the way in it must be one of TimeZoneCatalog.All (see
/// UpdateTenantTimezoneRequestValidator), not the looser "any string, unmatched falls back" treatment
/// signup's optional CountryCode/Timezone fields get - a tenant deliberately changing this expects it
/// to actually take effect, not silently degrade.</summary>
public record UpdateTenantTimezoneRequest(string Timezone);

/// <summary>Body of PUT the tenant's own country - same "must actually take effect, not silently
/// degrade" reasoning as UpdateTenantTimezoneRequest: on the way in it must be one of
/// RegionalPricingCatalog.All (see UpdateTenantCountryRequestValidator), unlike signup's looser
/// optional CountryCode. This is what a tenant whose plan is showing the wrong currency (see
/// RegionalPricingCatalog.Resolve) uses to fix it themselves.</summary>
public record UpdateTenantCountryRequest(string CountryCode);
