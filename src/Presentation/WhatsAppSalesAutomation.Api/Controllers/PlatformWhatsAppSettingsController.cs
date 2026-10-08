using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's WhatsApp page - PlatformSuperAdmin-only. The number the PLATFORM sends tenant notices from (plan
/// expiring, credits running out). Stored in the database only, the access token encrypted.</summary>
[ApiController]
[Route("api/v1/platform/whatsapp-settings")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformWhatsAppSettingsController : ControllerBase
{
    private readonly IPlatformWhatsAppSettingsService _settings;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformWhatsAppSettingsController(IPlatformWhatsAppSettingsService settings, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _settings = settings;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    /// <summary>The access token is masked: only whether one is stored and its last four characters come back.</summary>
    [HttpGet]
    public async Task<ActionResult<PlatformWhatsAppSettingsDto>> Get(CancellationToken cancellationToken)
        => Ok(await _settings.GetAsync(cancellationToken));

    /// <summary>Takes effect on the next request, no restart. A null access token keeps the stored one; an empty string clears it.</summary>
    [HttpPut]
    public async Task<ActionResult<PlatformWhatsAppSettingsDto>> Update([FromBody] UpdatePlatformWhatsAppSettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _settings.UpdateAsync(request, _currentUser.UserId, cancellationToken);

        // Names what changed, never the token.
        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty,
            PlatformAuditActions.PlatformWhatsAppSettingsUpdated,
            details: $"Platform WhatsApp number {(result.IsConfigured ? "on" : "off")} (phone number id {(result.PhoneNumberId.Length == 0 ? "none" : result.PhoneNumberId)}); " +
                     (request.AccessToken is null ? "access token unchanged" : "access token updated"),
            cancellationToken: cancellationToken);

        return Ok(result);
    }

    /// <summary>Asks Meta about the SAVED number and token (and the Business Account id) without sending any message. Always 200: the result says whether it worked.</summary>
    [HttpPost("verify")]
    public async Task<ActionResult<DeliveryTestResultDto>> Verify([FromServices] IPlatformWhatsAppVerifier verifier, CancellationToken cancellationToken)
        => Ok(await verifier.VerifyAsync(cancellationToken));

    /// <summary>Sends Meta's sample template from the saved number to prove the credentials work. Always 200: the result says whether it worked.</summary>
    [HttpPost("test")]
    public async Task<ActionResult<DeliveryTestResultDto>> Test([FromBody] SendPlatformWhatsAppTestRequest request, CancellationToken cancellationToken)
        => Ok(await _settings.SendTestAsync(request.To, cancellationToken));
}
