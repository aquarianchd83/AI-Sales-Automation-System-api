using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Platform Dashboard (spec item #1) - PlatformSuperAdmin-only.</summary>
[ApiController]
[Route("api/v1/platform/dashboard")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformDashboardController : ControllerBase
{
    private readonly IPlatformDashboardService _dashboardService;

    public PlatformDashboardController(IPlatformDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    [HttpGet]
    public async Task<ActionResult<PlatformDashboardDto>> Get(CancellationToken cancellationToken)
        => Ok(await _dashboardService.GetAsync(cancellationToken));
}
