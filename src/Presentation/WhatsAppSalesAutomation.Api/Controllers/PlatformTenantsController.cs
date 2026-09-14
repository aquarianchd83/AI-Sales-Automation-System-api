using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Tenants screen (spec item #2) - PlatformSuperAdmin-only.</summary>
[ApiController]
[Route("api/v1/platform/tenants")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformTenantsController : ControllerBase
{
    private readonly IPlatformTenantService _tenantService;
    private readonly ICurrentUserService _currentUser;

    public PlatformTenantsController(IPlatformTenantService tenantService, ICurrentUserService currentUser)
    {
        _tenantService = tenantService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<PlatformTenantListItemDto>>> GetPaged(
        [FromQuery] PlatformTenantQuery query, CancellationToken cancellationToken)
        => Ok(await _tenantService.GetPagedAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PlatformTenantDetailDto>> GetDetail(Guid id, CancellationToken cancellationToken)
        => Ok(await _tenantService.GetDetailAsync(id, cancellationToken));

    /// <summary>Operator-initiated tenant creation - see CreatePlatformTenantRequest's own doc
    /// comment for how this differs from self-serve signup.</summary>
    [HttpPost]
    public async Task<ActionResult<PlatformTenantDetailDto>> Create([FromBody] CreatePlatformTenantRequest request, CancellationToken cancellationToken)
    {
        var result = await _tenantService.CreateAsync(request, ActorUserId, ActorEmail, cancellationToken);
        return CreatedAtAction(nameof(GetDetail), new { id = result.Id }, result);
    }

    [HttpPost("{id:guid}/suspend")]
    public async Task<IActionResult> Suspend(Guid id, CancellationToken cancellationToken)
    {
        await _tenantService.SuspendAsync(id, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken)
    {
        await _tenantService.ReactivateAsync(id, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    /// <summary>Terminal status change, not a physical delete - see TenantStatus.Deleted's own doc
    /// comment.</summary>
    [HttpPost("{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _tenantService.DeleteAsync(id, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    /// <summary>Issues a short-lived, no-refresh-token access token for this tenant's admin - audited,
    /// time-boxed, per the spec's Impersonate action.</summary>
    [HttpPost("{id:guid}/impersonate")]
    public async Task<ActionResult<ImpersonationSessionDto>> Impersonate(Guid id, CancellationToken cancellationToken)
        => Ok(await _tenantService.ImpersonateAsync(id, ActorUserId, ActorEmail, cancellationToken));

    [HttpPut("{id:guid}/plan")]
    public async Task<IActionResult> OverridePlan(Guid id, [FromBody] OverrideTenantPlanRequest request, CancellationToken cancellationToken)
    {
        await _tenantService.OverridePlanAsync(id, request, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    /// <summary>Support-facing override of one tenant's timezone - see
    /// IPlatformTenantService.UpdateTimezoneAsync's own doc comment.</summary>
    [HttpPut("{id:guid}/timezone")]
    public async Task<ActionResult<TenantProfileDto>> UpdateTimezone(Guid id, [FromBody] UpdateTenantTimezoneRequest request, CancellationToken cancellationToken)
        => Ok(await _tenantService.UpdateTimezoneAsync(id, request, ActorUserId, ActorEmail, cancellationToken));

    /// <summary>Support-facing override of one tenant's country (and therefore plan pricing currency -
    /// see Application.Billing.RegionalPricingCatalog.Resolve) - see
    /// IPlatformTenantService.UpdateCountryAsync's own doc comment.</summary>
    [HttpPut("{id:guid}/country")]
    public async Task<ActionResult<TenantProfileDto>> UpdateCountry(Guid id, [FromBody] UpdateTenantCountryRequest request, CancellationToken cancellationToken)
        => Ok(await _tenantService.UpdateCountryAsync(id, request, ActorUserId, ActorEmail, cancellationToken));

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
