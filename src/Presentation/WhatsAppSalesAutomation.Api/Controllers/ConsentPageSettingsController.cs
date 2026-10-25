using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's WhatsApp Consent Page Settings: the product information and consent wording customers see before they agree to WhatsApp
/// messages. Admin only, and always the signed-in tenant's own - there is no tenant id in the route or the body to tamper with.
/// </summary>
[ApiController]
[Route("api/v1/consent-page-settings")]
[Authorize(Roles = AppRoles.Admin)]
public class ConsentPageSettingsController : ControllerBase
{
    private readonly IConsentPageSettingsService _settings;

    public ConsentPageSettingsController(IConsentPageSettingsService settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public async Task<ActionResult<ConsentPageSettingsDto>> Get(CancellationToken cancellationToken)
        => Ok(await _settings.GetAsync(cancellationToken));

    /// <summary>Saves the draft. It may be incomplete; it never changes what customers see until it is published.</summary>
    [HttpPut]
    public async Task<ActionResult<ConsentPageSettingsDto>> Save([FromBody] SaveConsentPageSettingsRequest request, CancellationToken cancellationToken)
        => Ok(await _settings.SaveAsync(request, cancellationToken));

    /// <summary>The saved draft as a customer would see it. Nothing is sent or stored.</summary>
    [HttpGet("preview")]
    public async Task<ActionResult<ConsentPagePreviewDto>> Preview(CancellationToken cancellationToken)
        => Ok(await _settings.PreviewAsync(cancellationToken));

    /// <summary>Publishes the saved draft as a new version. 400 with the fields to fix when it is incomplete.</summary>
    [HttpPost("publish")]
    public async Task<ActionResult<ConsentPageSettingsDto>> Publish(CancellationToken cancellationToken)
        => Ok(await _settings.PublishAsync(cancellationToken));

    [HttpPost("deactivate")]
    public async Task<ActionResult<ConsentPageSettingsDto>> Deactivate(CancellationToken cancellationToken)
        => Ok(await _settings.SetActiveAsync(false, cancellationToken));

    [HttpPost("activate")]
    public async Task<ActionResult<ConsentPageSettingsDto>> Activate(CancellationToken cancellationToken)
        => Ok(await _settings.SetActiveAsync(true, cancellationToken));
}
