using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Tenancy;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Unauthenticated, cosmetic-only tenant lookup - see <see cref="ITenantService.GetBySlugAsync"/>.</summary>
[ApiController]
[Route("api/v1/tenants")]
[AllowAnonymous]
public class TenantsController : ControllerBase
{
    private readonly ITenantService _tenantService;

    public TenantsController(ITenantService tenantService)
    {
        _tenantService = tenantService;
    }

    [HttpGet("by-slug/{slug}")]
    public async Task<ActionResult<TenantPublicDto>> GetBySlug(string slug, CancellationToken cancellationToken)
    {
        var result = await _tenantService.GetBySlugAsync(slug, cancellationToken);
        return Ok(result);
    }
}
