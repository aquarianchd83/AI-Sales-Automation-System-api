using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// A tenant's own self-service settings screen: pasting in their Meta WhatsApp Business Account
/// credentials and choosing/keying an AI provider - the BYO-WABA/BYO-API-key equivalent of
/// SettingsController, but scoped to the caller's own tenant (via ITenantContext) rather than
/// platform-wide. SuperAdmin/Admin-only, same reasoning as SettingsController: both sections hold real
/// credentials. A full Meta OAuth embedded-signup flow for WhatsApp is out of scope - this is manual
/// token/phone-number-ID entry.
/// </summary>
[ApiController]
[Route("api/v1/tenant-settings")]
[Authorize(Roles = $"{AppRoles.SuperAdmin},{AppRoles.Admin}")]
public class TenantSettingsController : ControllerBase
{
    private readonly ITenantWhatsAppConfigProvider _whatsAppConfigProvider;
    private readonly ITenantAiConfigProvider _aiConfigProvider;
    private readonly ICurrentUserService _currentUser;

    public TenantSettingsController(
        ITenantWhatsAppConfigProvider whatsAppConfigProvider, ITenantAiConfigProvider aiConfigProvider, ICurrentUserService currentUser)
    {
        _whatsAppConfigProvider = whatsAppConfigProvider;
        _aiConfigProvider = aiConfigProvider;
        _currentUser = currentUser;
    }

    /// <summary>Null (not 404) when the tenant has never configured a WABA - "not connected yet" is a
    /// normal state for a new trial tenant, not a missing resource.</summary>
    [HttpGet("whatsapp")]
    public async Task<ActionResult<TenantWhatsAppConfigDto?>> GetWhatsAppConfig(CancellationToken cancellationToken)
        => Ok(await _whatsAppConfigProvider.GetConfigForCurrentTenantAsync(cancellationToken));

    [HttpPut("whatsapp")]
    public async Task<ActionResult<TenantWhatsAppConfigDto>> SaveWhatsAppConfig(
        [FromBody] UpdateTenantWhatsAppConfigRequest request, CancellationToken cancellationToken)
        => Ok(await _whatsAppConfigProvider.SaveConfigForCurrentTenantAsync(request, _currentUser.UserId, cancellationToken));

    /// <summary>Null when the tenant has never configured one - defaults to Simulated for both
    /// Provider and EmbeddingProvider, same as a brand-new trial tenant's WhatsApp config.</summary>
    [HttpGet("ai")]
    public async Task<ActionResult<TenantAiProviderConfigDto?>> GetAiConfig(CancellationToken cancellationToken)
        => Ok(await _aiConfigProvider.GetConfigForCurrentTenantAsync(cancellationToken));

    [HttpPut("ai")]
    public async Task<ActionResult<TenantAiProviderConfigDto>> SaveAiConfig(
        [FromBody] UpdateTenantAiProviderConfigRequest request, CancellationToken cancellationToken)
        => Ok(await _aiConfigProvider.SaveConfigForCurrentTenantAsync(request, _currentUser.UserId, cancellationToken));
}
