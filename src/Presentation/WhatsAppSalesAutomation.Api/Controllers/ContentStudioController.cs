using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's AI Content Studio: what it has spent and on what, and the studio's own settings. Admin only - the
/// charge history is billing information, exactly like <c>BillingController</c>'s. Every query is for the calling
/// tenant only; a PlatformSuperAdmin has no tenant in scope and is refused by the role gate.
/// </summary>
[ApiController]
[Route("api/v1/content-studio")]
[Authorize(Roles = AppRoles.Admin)]
public class ContentStudioController : ControllerBase
{
    private readonly IStudioMeteringService _metering;
    private readonly IStudioSettingsService _settings;
    private readonly ITenantContext _tenantContext;

    public ContentStudioController(IStudioMeteringService metering, IStudioSettingsService settings, ITenantContext tenantContext)
    {
        _metering = metering;
        _settings = settings;
        _tenantContext = tenantContext;
    }

    /// <summary>Whether the studio is switched on, the tenant's studio balances, and the last 30 days' totals.</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<StudioOverviewDto>> GetOverview(CancellationToken cancellationToken)
        => Ok(await _metering.GetOverviewAsync(RequireTenantId(), cancellationToken));

    /// <summary>Every metered step the tenant has run - charged, completed, refunded or refused - newest first.</summary>
    [HttpGet("activity")]
    public async Task<ActionResult<PagedResult<StudioActivityDto>>> GetActivity([FromQuery] StudioActivityQuery query, CancellationToken cancellationToken)
        => Ok(await _metering.GetActivityAsync(RequireTenantId(), query, cancellationToken));

    [HttpGet("settings")]
    public async Task<ActionResult<StudioSettingsDto>> GetSettings(CancellationToken cancellationToken)
        => Ok(await _settings.GetAsync(RequireTenantId(), cancellationToken));

    [HttpPut("settings")]
    public async Task<ActionResult<StudioSettingsDto>> UpdateSettings([FromBody] UpdateStudioSettingsRequest request, CancellationToken cancellationToken)
        => Ok(await _settings.UpdateAsync(RequireTenantId(), request, cancellationToken));

    /// <summary>The role gate guarantees a real tenant is in scope; this just gives that guarantee a non-nullable type.</summary>
    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content studio request has no tenant in scope.");
}
