using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.SocialAds;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's Facebook / Instagram ad connection, which feeds the social-media side of the Revenue report. Admin only:
/// connecting hands this app read access to the tenant's ad spend. The access token is never returned by any endpoint.
/// </summary>
[ApiController]
[Route("api/v1/social-ads")]
[Authorize(Roles = AppRoles.Admin)]
public class SocialAdsController : ControllerBase
{
    private readonly ISocialAdsService _socialAds;

    public SocialAdsController(ISocialAdsService socialAds)
    {
        _socialAds = socialAds;
    }

    [HttpGet]
    public async Task<ActionResult<SocialAdsStatusDto>> GetStatus(CancellationToken cancellationToken)
        => Ok(await _socialAds.GetStatusAsync(cancellationToken));

    /// <summary>The Facebook login address to send the tenant to. <c>redirectUri</c> is the page Facebook returns them to.</summary>
    [HttpGet("connect-url")]
    public async Task<ActionResult<SocialAdsConnectUrlDto>> GetConnectUrl([FromQuery] string redirectUri, CancellationToken cancellationToken)
        => Ok(await _socialAds.GetConnectUrlAsync(redirectUri ?? string.Empty, cancellationToken));

    [HttpPost("connect")]
    public async Task<ActionResult<SocialAdsStatusDto>> Connect([FromBody] CompleteSocialAdsConnectRequest request, CancellationToken cancellationToken)
        => Ok(await _socialAds.CompleteConnectAsync(request, cancellationToken));

    [HttpPost("select-account")]
    public async Task<ActionResult<SocialAdsStatusDto>> SelectAccount([FromBody] SelectSocialAdAccountRequest request, CancellationToken cancellationToken)
        => Ok(await _socialAds.SelectAccountAsync(request, cancellationToken));

    [HttpPost("sync")]
    public async Task<ActionResult<SocialAdsStatusDto>> Sync(CancellationToken cancellationToken)
        => Ok(await _socialAds.SyncNowAsync(cancellationToken));

    [HttpDelete]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        await _socialAds.DisconnectAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>For a tenant with no ad account to connect: typed-in monthly ad spend.</summary>
    [HttpGet("manual-spend")]
    public async Task<ActionResult<IReadOnlyList<ManualAdSpendDto>>> GetManualSpend(CancellationToken cancellationToken)
        => Ok(await _socialAds.GetManualSpendAsync(cancellationToken));

    [HttpPut("manual-spend")]
    public async Task<ActionResult<ManualAdSpendDto?>> SaveManualSpend([FromBody] SaveManualAdSpendRequest request, CancellationToken cancellationToken)
        => Ok(await _socialAds.SaveManualSpendAsync(request, cancellationToken));
}
