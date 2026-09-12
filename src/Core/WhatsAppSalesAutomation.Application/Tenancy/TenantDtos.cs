namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>
/// The only fields safe to expose pre-login (see <see cref="ITenantService.GetBySlugAsync"/>) -
/// cosmetic branding for the frontend's login page, nothing that reveals tenant-internal data.
/// </summary>
public record TenantPublicDto(string Name, string Slug);

/// <summary>The calling tenant's own editable profile fields - just Timezone for now (see
/// ITenantService.GetProfileForCurrentTenantAsync). Always the effective value, defaulting to
/// TimeZoneCatalog.DefaultId the same way ITenantTimeZoneProvider does, so this never returns
/// null/empty even before the tenant has ever set one explicitly.</summary>
public record TenantProfileDto(string Timezone);

/// <summary>Body of PUT the tenant's own timezone - see TenantProfileDto's own doc comment for why
/// this is never blank on the way out; on the way in it must be one of TimeZoneCatalog.All (see
/// UpdateTenantTimezoneRequestValidator), not the looser "any string, unmatched falls back" treatment
/// signup's optional CountryCode/Timezone fields get - a tenant deliberately changing this expects it
/// to actually take effect, not silently degrade.</summary>
public record UpdateTenantTimezoneRequest(string Timezone);
