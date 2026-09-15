using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>A tenant's own editable profile - business details (company and product names, industry,
/// description, contact details, domain keywords) plus Timezone and Country. Deliberately separate from
/// TenantSettingsController, whose own doc comment frames it as a read-only WhatsApp/AI status view;
/// nothing here is WhatsApp/AI-related or read-only, so it doesn't belong bolted onto that controller's
/// documented scope. A PlatformSuperAdmin can also change Timezone or Country from the Platform Admin
/// Console (see PlatformTenantsController's PUT {id}/timezone and PUT {id}/country) - both write the same
/// columns, this is not the sole owner of either.</summary>
[ApiController]
[Route("api/v1/tenant-profile")]
[Authorize(Roles = AppRoles.Admin)]
public class TenantProfileController : ControllerBase
{
    private readonly ITenantService _tenantService;

    public TenantProfileController(ITenantService tenantService)
    {
        _tenantService = tenantService;
    }

    [HttpGet]
    public async Task<ActionResult<TenantProfileDto>> Get(CancellationToken cancellationToken)
        => Ok(await _tenantService.GetProfileForCurrentTenantAsync(cancellationToken));

    [HttpPut]
    public async Task<ActionResult<TenantProfileDto>> UpdateBusinessProfile([FromBody] UpdateTenantBusinessProfileRequest request, CancellationToken cancellationToken)
        => Ok(await _tenantService.UpdateBusinessProfileForCurrentTenantAsync(request, cancellationToken));

    [HttpPut("timezone")]
    public async Task<ActionResult<TenantProfileDto>> UpdateTimezone([FromBody] UpdateTenantTimezoneRequest request, CancellationToken cancellationToken)
        => Ok(await _tenantService.UpdateTimezoneForCurrentTenantAsync(request, cancellationToken));

    [HttpPut("country")]
    public async Task<ActionResult<TenantProfileDto>> UpdateCountry([FromBody] UpdateTenantCountryRequest request, CancellationToken cancellationToken)
        => Ok(await _tenantService.UpdateCountryForCurrentTenantAsync(request, cancellationToken));
}
