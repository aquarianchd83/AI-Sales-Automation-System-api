using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Application.Consent;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// What a customer reaches from the link in the SMS. Anonymous: the secret in the link is the whole credential, and it alone says which tenant,
/// customer and request this is. There is deliberately no bearer token or cookie, so there is no ambient credential for a cross-site request
/// to ride on (CSRF does not apply); the decision must be a JSON POST carrying the secret.
///
/// GET only reads. SMS scanners, link-preview bots and browsers prefetch links, so opening one must never record consent.
/// </summary>
[ApiController]
[Route("api/v1/public/consent")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingServiceExtensions.ConsentPagePolicy)]
public class PublicConsentController : ControllerBase
{
    private readonly IConsentPageService _consent;

    public PublicConsentController(IConsentPageService consent)
    {
        _consent = consent;
    }

    [HttpGet("{token}")]
    public async Task<ActionResult<PublicConsentPageDto>> Get(string token, CancellationToken cancellationToken)
    {
        NoStore();
        var page = await _consent.GetAsync(token, cancellationToken);
        return page.State switch
        {
            PublicConsentStates.Invalid => NotFound(page),
            PublicConsentStates.Expired => StatusCode(StatusCodes.Status410Gone, page),
            _ => Ok(page)
        };
    }

    [HttpPost("{token}/decision")]
    public async Task<ActionResult<ConsentDecisionResultDto>> Decide(
        string token, [FromBody] ConsentDecisionRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        var client = new ConsentClientInfo(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
        var result = await _consent.DecideAsync(token, request, client, cancellationToken);

        return result.Outcome switch
        {
            ConsentDecisionOutcomes.Recorded or ConsentDecisionOutcomes.AlreadyRecorded => Ok(result),
            ConsentDecisionOutcomes.BadRequest => BadRequest(result),
            ConsentDecisionOutcomes.Invalid => NotFound(result),
            ConsentDecisionOutcomes.Expired => StatusCode(StatusCodes.Status410Gone, result),
            ConsentDecisionOutcomes.NotAllowed => StatusCode(StatusCodes.Status403Forbidden, result),
            _ => Conflict(result)
        };
    }

    /// <summary>A response about a private link is never cached by a browser or a proxy.</summary>
    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
