using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Revenue report - PlatformSuperAdmin-only, read-only. Built from payments; nothing here moves money.</summary>
[ApiController]
[Route("api/v1/platform/revenue")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformRevenueController : ControllerBase
{
    private readonly IPlatformRevenueService _revenue;

    public PlatformRevenueController(IPlatformRevenueService revenue)
    {
        _revenue = revenue;
    }

    [HttpGet]
    public async Task<ActionResult<PlatformRevenueReportDto>> Get([FromQuery] PlatformRevenueQuery query, CancellationToken cancellationToken)
        => Ok(await _revenue.GetAsync(query, cancellationToken));
}
