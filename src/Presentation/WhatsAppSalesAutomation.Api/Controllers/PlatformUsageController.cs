using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Usage & Quotas screen (spec item #4) - PlatformSuperAdmin-only.</summary>
[ApiController]
[Route("api/v1/platform/usage")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformUsageController : ControllerBase
{
    private readonly IPlatformUsageService _usageService;

    public PlatformUsageController(IPlatformUsageService usageService)
    {
        _usageService = usageService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<PlatformTenantUsageDto>>> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _usageService.GetPagedAsync(request, cancellationToken));
}
