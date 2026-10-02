using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Authentication Delivery page - PlatformSuperAdmin-only. The SMTP server, the MSG91 SMS account
/// and the web app's address that sign-in emails and texts depend on. Stored in the database only (the secrets encrypted).</summary>
[ApiController]
[Route("api/v1/platform/delivery-settings")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformDeliverySettingsController : ControllerBase
{
    private readonly IPlatformDeliverySettingsService _settings;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformDeliverySettingsController(
        IPlatformDeliverySettingsService settings,
        IPlatformAuditService auditService,
        ICurrentUserService currentUser)
    {
        _settings = settings;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    /// <summary>Secrets are masked: only whether one is stored and its last four characters come back.</summary>
    [HttpGet]
    public async Task<ActionResult<PlatformDeliverySettingsDto>> Get(CancellationToken cancellationToken)
        => Ok(await _settings.GetAsync(cancellationToken));

    /// <summary>Takes effect on the next request, no restart. A null password / auth key keeps the stored one; an empty string clears it.</summary>
    [HttpPut]
    public async Task<ActionResult<PlatformDeliverySettingsDto>> Update([FromBody] UpdatePlatformDeliverySettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _settings.UpdateAsync(request, _currentUser.UserId, cancellationToken);

        // Names what changed, never a secret value.
        var secrets = new List<string>();
        if (request.SmtpPassword is not null) secrets.Add("SMTP password");
        if (request.SmsAuthKey is not null) secrets.Add("SMS auth key");
        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, PlatformAuditActions.DeliverySettingsUpdated,
            details: $"Email {(result.IsEmailConfigured ? "on" : "off")} ({result.SmtpHost}), SMS {(result.IsSmsConfigured ? "on" : "off")}, web app address '{result.PublicUrl}'; " +
                     (secrets.Count == 0 ? "secrets unchanged" : $"{string.Join(" and ", secrets)} updated"),
            cancellationToken: cancellationToken);

        return Ok(result);
    }

    /// <summary>Sends one real email with the settings in the request, saved or not. Always 200: the result says whether it worked.</summary>
    [HttpPost("test-email")]
    public async Task<ActionResult<DeliveryTestResultDto>> TestEmail([FromBody] SendDeliveryTestRequest request, CancellationToken cancellationToken)
        => Ok(await _settings.TestEmailAsync(request, cancellationToken));

    /// <summary>Sends one real text (with a sample code) with the settings in the request, saved or not. Always 200.</summary>
    [HttpPost("test-sms")]
    public async Task<ActionResult<DeliveryTestResultDto>> TestSms([FromBody] SendDeliveryTestRequest request, CancellationToken cancellationToken)
        => Ok(await _settings.TestSmsAsync(request, cancellationToken));
}
