using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Onboarding;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's onboarding: nine sequential, weighted steps. Every tenant user may read it - the Admin to work
/// through it, everyone else to see how far setup has got while they wait. Reading it also records any step that
/// has just been completed, so the screen and the stored state never disagree.
/// </summary>
[ApiController]
[Route("api/v1/onboarding")]
[Authorize]
public class OnboardingController : ControllerBase
{
    private readonly IOnboardingService _service;

    public OnboardingController(IOnboardingService service)
    {
        _service = service;
    }

    /// <summary>204 for a caller with no tenant (a PlatformSuperAdmin): there is nothing to onboard.</summary>
    [HttpGet]
    public async Task<ActionResult<OnboardingStatusDto>> Get(CancellationToken cancellationToken)
        => await _service.GetStatusAsync(cancellationToken) is { } status ? Ok(status) : NoContent();
}
