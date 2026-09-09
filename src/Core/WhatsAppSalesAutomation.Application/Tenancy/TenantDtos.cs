namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>
/// The only fields safe to expose pre-login (see <see cref="ITenantService.GetBySlugAsync"/>) -
/// cosmetic branding for the frontend's login page, nothing that reveals tenant-internal data.
/// </summary>
public record TenantPublicDto(string Name, string Slug);
