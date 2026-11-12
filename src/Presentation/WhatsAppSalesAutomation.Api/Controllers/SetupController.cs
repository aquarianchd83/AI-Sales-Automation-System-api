using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Application.Auth;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// First-run setup: lets the operator create the very first platform super admin on a fresh database. Anonymous on
/// purpose (nobody can sign in yet), so it is guarded three ways - it needs the configured Setup:BootstrapKey, it
/// closes for good once any platform super admin exists, and it sits behind the strict auth rate limit.
/// </summary>
[ApiController]
[Route("api/v1/setup")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingServiceExtensions.AuthPolicy)]
public class SetupController : ControllerBase
{
    private readonly IPlatformSetupService _setup;

    public SetupController(IPlatformSetupService setup) => _setup = setup;

    [HttpGet("status")]
    public async Task<ActionResult<PlatformSetupStatusDto>> GetStatus(CancellationToken cancellationToken)
        => Ok(await _setup.GetStatusAsync(cancellationToken));

    [HttpPost("super-admin")]
    public async Task<IActionResult> CreateSuperAdmin([FromBody] CreatePlatformSuperAdminRequest request, CancellationToken cancellationToken)
    {
        await _setup.CreateFirstSuperAdminAsync(request, cancellationToken);
        return NoContent();
    }
}
