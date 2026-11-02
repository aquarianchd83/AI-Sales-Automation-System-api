using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's WhatsApp provider selector - PlatformSuperAdmin-only. Which integration the platform offers tenants:
/// the Meta Cloud API or the QR-code bridge. Stores the choice only; no secrets pass through here.</summary>
[ApiController]
[Route("api/v1/platform/whatsapp-provider")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformWhatsAppProviderController : ControllerBase
{
    private readonly IWhatsAppProviderSettingsService _settings;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformWhatsAppProviderController(IWhatsAppProviderSettingsService settings, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _settings = settings;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<WhatsAppProviderSettingsDto>> Get(CancellationToken cancellationToken)
        => Ok(await _settings.GetAsync(cancellationToken));

    /// <summary>Invalid or unavailable choices are refused (400) and the active provider is left as it was.</summary>
    [HttpPut]
    public async Task<ActionResult<WhatsAppProviderSettingsDto>> Update([FromBody] UpdateWhatsAppProviderRequest request, CancellationToken cancellationToken)
    {
        var change = await _settings.UpdateAsync(request, _currentUser.UserId, cancellationToken);

        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty,
            PlatformAuditActions.WhatsAppProviderChanged,
            details: $"WhatsApp provider {change.Previous} -> {change.Settings.Active}",
            cancellationToken: cancellationToken);

        return Ok(change.Settings);
    }

    /// <summary>One tenant's own provider choice, what it effectively uses, and the platform default it falls back to.</summary>
    [HttpGet("tenants/{tenantId:guid}")]
    public async Task<ActionResult<TenantWhatsAppProviderDto>> GetForTenant(Guid tenantId, CancellationToken cancellationToken)
        => Ok(await _settings.GetForTenantAsync(tenantId, cancellationToken));

    /// <summary>A null provider clears the tenant's own choice so it follows the platform default again.</summary>
    [HttpPut("tenants/{tenantId:guid}")]
    public async Task<ActionResult<TenantWhatsAppProviderDto>> UpdateForTenant(
        Guid tenantId, [FromBody] UpdateTenantWhatsAppProviderRequest request, CancellationToken cancellationToken)
    {
        var result = await _settings.SetForTenantAsync(tenantId, request, _currentUser.UserId, cancellationToken);

        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty,
            PlatformAuditActions.WhatsAppProviderChanged,
            details: $"WhatsApp provider for tenant {tenantId}: {request.Provider ?? "platform default"} (effective {result.Effective})",
            cancellationToken: cancellationToken);

        return Ok(result);
    }
}
