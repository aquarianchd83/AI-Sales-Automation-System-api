using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>A tenant's own editable profile - just Timezone for now. Deliberately separate from
/// TenantSettingsController, whose own doc comment frames it as a read-only WhatsApp/AI status view;
/// Timezone is neither WhatsApp/AI-related nor read-only, so it doesn't belong bolted onto that
/// controller's documented scope. A PlatformSuperAdmin can also change a tenant's timezone from the
/// Platform Admin Console (see PlatformTenantsController's PUT {id}/timezone) - both write the same
/// column, this is not the sole owner of it.</summary>
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

    [HttpPut("timezone")]
    public async Task<ActionResult<TenantProfileDto>> UpdateTimezone([FromBody] UpdateTenantTimezoneRequest request, CancellationToken cancellationToken)
        => Ok(await _tenantService.UpdateTimezoneForCurrentTenantAsync(request, cancellationToken));
}
