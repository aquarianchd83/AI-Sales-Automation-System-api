using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>A tenant's own credit usage: each action that spent credits, and which ones the platform covered ("covered by the platform").
/// The platform's wallet is never shown, only whether it paid.</summary>
[ApiController]
[Route("api/v1/billing/credit-usage")]
[Authorize(Roles = AppRoles.Admin)]
public class TenantCreditUsageController : ControllerBase
{
    private readonly ICreditPayerService _credits;
    private readonly ITenantContext _tenantContext;

    public TenantCreditUsageController(ICreditPayerService credits, ITenantContext tenantContext)
    {
        _credits = credits;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<TenantCreditUsageDto>>> Get([FromQuery] TenantCreditUsageQuery query, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("No tenant in scope.");
        return Ok(await _credits.GetTenantUsageAsync(tenantId, query, cancellationToken));
    }
}
