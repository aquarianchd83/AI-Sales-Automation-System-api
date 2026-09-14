using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// A tenant's own read-only view of its WhatsApp Business Account connection, AI provider setup, and
/// message usage. This used to also be where a tenant's own Admin pasted in credentials and chose an
/// AI provider (Phase B) - that write access has moved to the Platform Admin Console exclusively (see
/// PlatformTenantConfigController's own doc comment for why): both hold real, security-sensitive
/// credentials that a PlatformSuperAdmin now owns end to end, the same reasoning SettingsController
/// already applies to the platform-global catalog. A tenant's own Admin can still see whether they're
/// connected and what's configured (masked, same as before) - just not change it.
/// </summary>
[ApiController]
[Route("api/v1/tenant-settings")]
[Authorize(Roles = AppRoles.Admin)]
public class TenantSettingsController : ControllerBase
{
    private readonly ITenantWhatsAppConfigProvider _whatsAppConfigProvider;
    private readonly ITenantAiConfigProvider _aiConfigProvider;
    private readonly IPlanLimitsService _planLimits;
    private readonly ITenantContext _tenantContext;

    public TenantSettingsController(
        ITenantWhatsAppConfigProvider whatsAppConfigProvider,
        ITenantAiConfigProvider aiConfigProvider,
        IPlanLimitsService planLimits,
        ITenantContext tenantContext)
    {
        _whatsAppConfigProvider = whatsAppConfigProvider;
        _aiConfigProvider = aiConfigProvider;
        _planLimits = planLimits;
        _tenantContext = tenantContext;
    }

    /// <summary>Null (not 404) when the tenant has never configured a WABA - "not connected yet" is a
    /// normal state for a new trial tenant, not a missing resource.</summary>
    [HttpGet("whatsapp")]
    public async Task<ActionResult<TenantWhatsAppConfigDto?>> GetWhatsAppConfig(CancellationToken cancellationToken)
        => Ok(await _whatsAppConfigProvider.GetConfigForCurrentTenantAsync(cancellationToken));

    /// <summary>Null when the tenant has never configured one - defaults to Simulated for both
    /// Provider and EmbeddingProvider, same as a brand-new trial tenant's WhatsApp config.</summary>
    [HttpGet("ai")]
    public async Task<ActionResult<TenantAiProviderConfigDto?>> GetAiConfig(CancellationToken cancellationToken)
        => Ok(await _aiConfigProvider.GetConfigForCurrentTenantAsync(cancellationToken));

    /// <summary>How much of this month's WhatsApp message quota the tenant has used - see
    /// IPlanLimitsService.GetMessageUsageAsync's own doc comment.</summary>
    [HttpGet("usage")]
    public async Task<ActionResult<TenantMessageUsageDto>> GetUsage(CancellationToken cancellationToken)
        => Ok(await _planLimits.GetMessageUsageAsync(RequireTenantId(), cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated tenant-settings request has no tenant in scope.");
}
