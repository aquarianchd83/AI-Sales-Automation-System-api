using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The Platform Admin Console's side of a tenant's WhatsApp Business Account credentials
/// - PlatformSuperAdmin-only. WhatsApp is shared ownership: the tenant's own Admin can also save and
/// verify it (TenantSettingsController, the onboarding wizard's WhatsApp step); both write the same row.
/// The AI provider is platform-wide (Configuration screen), not per tenant.
/// </summary>
[ApiController]
[Route("api/v1/platform/tenants/{tenantId:guid}")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformTenantConfigController : ControllerBase
{
    private readonly ITenantWhatsAppConfigProvider _whatsAppConfigProvider;
    private readonly ITenantConfigOverrideProvider _configOverrideProvider;
    private readonly IPlatformAuditService _auditService;
    private readonly IPlatformJobService _jobService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<PlatformTenantConfigController> _logger;

    public PlatformTenantConfigController(
        ITenantWhatsAppConfigProvider whatsAppConfigProvider,
        ITenantConfigOverrideProvider configOverrideProvider,
        IPlatformAuditService auditService,
        IPlatformJobService jobService,
        ICurrentUserService currentUser,
        ILogger<PlatformTenantConfigController> logger)
    {
        _whatsAppConfigProvider = whatsAppConfigProvider;
        _configOverrideProvider = configOverrideProvider;
        _auditService = auditService;
        _jobService = jobService;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>Null (not 404) when the tenant has never configured a WABA - a normal state, not a
    /// missing resource.</summary>
    [HttpGet("whatsapp-config")]
    public async Task<ActionResult<TenantWhatsAppConfigDto?>> GetWhatsAppConfig(Guid tenantId, CancellationToken cancellationToken)
        => Ok(await _whatsAppConfigProvider.GetConfigForTenantAsync(tenantId, cancellationToken));

    [HttpPut("whatsapp-config")]
    public async Task<ActionResult<TenantWhatsAppConfigDto>> SaveWhatsAppConfig(
        Guid tenantId, [FromBody] UpdateTenantWhatsAppConfigRequest request, CancellationToken cancellationToken)
    {
        var result = await _whatsAppConfigProvider.SaveConfigForTenantAsync(tenantId, request, ActorUserId, cancellationToken);

        await _auditService.LogAsync(
            ActorUserId, ActorEmail, PlatformAuditActions.TenantWhatsAppConfigSaved, tenantId,
            details: $"Phone number ID: {result.PhoneNumberId}", cancellationToken: cancellationToken);

        if (!string.IsNullOrEmpty(request.AccessToken) || !string.IsNullOrEmpty(request.AppSecret))
            await StartTokenRefreshAsync(tenantId, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Exchanges a newly saved token right away instead of leaving it to the daily scheduled run. A token
    /// copied from Meta's Graph API Explorer is short-lived (about an hour), and Meta only exchanges a
    /// token for a 60-day one while it is still valid - by the next midnight run it has already expired
    /// and can never be refreshed. Goes through the same "Run now" path as the console, so a suspended
    /// tenant or a paused job is still respected and the run is audited; neither of those fails the save.
    /// </summary>
    private async Task StartTokenRefreshAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await _jobService.TriggerAsync(tenantId, TenantJobTypes.WhatsAppTokenRefresh, ActorUserId, ActorEmail, cancellationToken);
        }
        catch (Exception ex) when (ex is ConflictException or NotFoundException)
        {
            _logger.LogWarning("WhatsApp token refresh not started after saving credentials for tenant {TenantId}: {Reason}", tenantId, ex.Message);
        }
    }

    /// <summary>Asks Meta whether the saved credentials reach the phone number, and records the answer.</summary>
    [HttpPost("whatsapp-config/verify")]
    public async Task<ActionResult<TenantWhatsAppConfigDto>> VerifyWhatsAppConfig(
        Guid tenantId, [FromServices] ITenantWhatsAppConnectionVerifier verifier, CancellationToken cancellationToken)
        => Ok(await verifier.VerifyAsync(tenantId, cancellationToken));

    /// <summary>Removes the tenant's WhatsApp config entirely - back to "not connected."</summary>
    [HttpDelete("whatsapp-config")]
    public async Task<IActionResult> DeleteWhatsAppConfig(Guid tenantId, CancellationToken cancellationToken)
    {
        await _whatsAppConfigProvider.DeleteConfigForTenantAsync(tenantId, cancellationToken);
        await _auditService.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.TenantWhatsAppConfigDeleted, tenantId, cancellationToken: cancellationToken);
        return NoContent();
    }


    /// <summary>Every tenant-overridable Campaigns/Media/Messaging/Ai key, showing the platform
    /// default, this tenant's override (if any) and the effective value - see
    /// AppSettingDefinition.IsTenantOverridable's own doc comment for which keys these are and why.</summary>
    [HttpGet("config-overrides")]
    public async Task<ActionResult<IReadOnlyList<TenantSettingCategoryDto>>> GetConfigOverrides(Guid tenantId, CancellationToken cancellationToken)
        => Ok(await _configOverrideProvider.GetOverridesForTenantAsync(tenantId, cancellationToken));

    [HttpPut("config-overrides")]
    public async Task<ActionResult<IReadOnlyList<TenantSettingCategoryDto>>> SaveConfigOverrides(
        Guid tenantId, [FromBody] UpdateTenantSettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _configOverrideProvider.SaveOverridesForTenantAsync(tenantId, request, ActorUserId, cancellationToken);

        await _auditService.LogAsync(
            ActorUserId, ActorEmail, PlatformAuditActions.TenantConfigOverridesSaved, tenantId,
            details: $"{request.Values.Count} value(s) changed", cancellationToken: cancellationToken);

        return Ok(result);
    }

    /// <summary>Clears every override this tenant has across all four categories at once - back to the
    /// platform default for everything.</summary>
    [HttpDelete("config-overrides")]
    public async Task<IActionResult> DeleteConfigOverrides(Guid tenantId, CancellationToken cancellationToken)
    {
        await _configOverrideProvider.DeleteOverridesForTenantAsync(tenantId, cancellationToken);
        await _auditService.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.TenantConfigOverridesDeleted, tenantId, cancellationToken: cancellationToken);
        return NoContent();
    }

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
