using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Where a tenant's WhatsApp Business Account credentials and AI provider config now get created,
/// edited and deleted - PlatformSuperAdmin-only. Both used to be the tenant's own self-service
/// settings (Phase B, TenantSettingsController's PUT endpoints); that write access moved here because
/// both hold real, security-sensitive credentials (a Meta System User token; OpenAI/Anthropic/Google
/// API keys) whose correctness affects billing and platform-wide abuse exposure, not just the one
/// tenant - too important to leave to self-service. A tenant's own Admin keeps read-only visibility
/// via TenantSettingsController's GET endpoints, unchanged.
/// </summary>
[ApiController]
[Route("api/v1/platform/tenants/{tenantId:guid}")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformTenantConfigController : ControllerBase
{
    private readonly ITenantWhatsAppConfigProvider _whatsAppConfigProvider;
    private readonly ITenantAiConfigProvider _aiConfigProvider;
    private readonly ITenantConfigOverrideProvider _configOverrideProvider;
    private readonly IPlatformAuditService _auditService;
    private readonly IPlatformJobService _jobService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<PlatformTenantConfigController> _logger;

    public PlatformTenantConfigController(
        ITenantWhatsAppConfigProvider whatsAppConfigProvider,
        ITenantAiConfigProvider aiConfigProvider,
        ITenantConfigOverrideProvider configOverrideProvider,
        IPlatformAuditService auditService,
        IPlatformJobService jobService,
        ICurrentUserService currentUser,
        ILogger<PlatformTenantConfigController> logger)
    {
        _whatsAppConfigProvider = whatsAppConfigProvider;
        _aiConfigProvider = aiConfigProvider;
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

    /// <summary>Removes the tenant's WhatsApp config entirely - back to "not connected."</summary>
    [HttpDelete("whatsapp-config")]
    public async Task<IActionResult> DeleteWhatsAppConfig(Guid tenantId, CancellationToken cancellationToken)
    {
        await _whatsAppConfigProvider.DeleteConfigForTenantAsync(tenantId, cancellationToken);
        await _auditService.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.TenantWhatsAppConfigDeleted, tenantId, cancellationToken: cancellationToken);
        return NoContent();
    }

    /// <summary>Null when the tenant has never configured one - defaults to Simulated for both
    /// Provider and EmbeddingProvider.</summary>
    [HttpGet("ai-config")]
    public async Task<ActionResult<TenantAiProviderConfigDto?>> GetAiConfig(Guid tenantId, CancellationToken cancellationToken)
        => Ok(await _aiConfigProvider.GetConfigForTenantAsync(tenantId, cancellationToken));

    [HttpPut("ai-config")]
    public async Task<ActionResult<TenantAiProviderConfigDto>> SaveAiConfig(
        Guid tenantId, [FromBody] UpdateTenantAiProviderConfigRequest request, CancellationToken cancellationToken)
    {
        var result = await _aiConfigProvider.SaveConfigForTenantAsync(tenantId, request, ActorUserId, cancellationToken);

        await _auditService.LogAsync(
            ActorUserId, ActorEmail, PlatformAuditActions.TenantAiConfigSaved, tenantId,
            details: $"Provider: {result.Provider}, embedding: {result.EmbeddingProvider}", cancellationToken: cancellationToken);

        return Ok(result);
    }

    /// <summary>Removes the tenant's AI provider config entirely - back to the built-in Simulated
    /// defaults.</summary>
    [HttpDelete("ai-config")]
    public async Task<IActionResult> DeleteAiConfig(Guid tenantId, CancellationToken cancellationToken)
    {
        await _aiConfigProvider.DeleteConfigForTenantAsync(tenantId, cancellationToken);
        await _auditService.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.TenantAiConfigDeleted, tenantId, cancellationToken: cancellationToken);
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
