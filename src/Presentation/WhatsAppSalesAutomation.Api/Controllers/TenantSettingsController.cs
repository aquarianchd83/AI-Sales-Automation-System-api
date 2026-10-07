using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// A tenant's own settings: its WhatsApp Business Account connection (which the tenant's Admin and the Platform
/// Admin both own - either can save it and verify it against Meta), a read-only view of the AI provider setup
/// (platform-owned), and message usage and charges. Secrets are never returned, only whether each is set.
/// </summary>
[ApiController]
[Route("api/v1/tenant-settings")]
[Authorize(Roles = AppRoles.Admin)]
public class TenantSettingsController : ControllerBase
{
    private readonly ITenantWhatsAppConfigProvider _whatsAppConfigProvider;
    private readonly ITenantAiConfigProvider _aiConfigProvider;
    private readonly IPlanLimitsService _planLimits;
    private readonly ITenantChargesService _charges;
    private readonly ITenantContext _tenantContext;

    public TenantSettingsController(
        ITenantWhatsAppConfigProvider whatsAppConfigProvider,
        ITenantAiConfigProvider aiConfigProvider,
        IPlanLimitsService planLimits,
        ITenantChargesService charges,
        ITenantContext tenantContext)
    {
        _whatsAppConfigProvider = whatsAppConfigProvider;
        _aiConfigProvider = aiConfigProvider;
        _planLimits = planLimits;
        _charges = charges;
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

    /// <summary>The WhatsApp number the tenant gave for its customers to message - just the number. Connecting it to
    /// WhatsApp (credentials, verification) is the separate "WhatsApp connection" below.</summary>
    [HttpGet("whatsapp-number")]
    public async Task<ActionResult<TenantWhatsAppNumberDto>> GetWhatsAppNumber([FromServices] ITenantService tenants, CancellationToken cancellationToken)
        => Ok(await tenants.GetWhatsAppNumberForCurrentTenantAsync(cancellationToken));

    [HttpPut("whatsapp-number")]
    public async Task<ActionResult<TenantWhatsAppNumberDto>> SaveWhatsAppNumber(
        [FromBody] UpdateTenantWhatsAppNumberRequest request, [FromServices] ITenantService tenants, CancellationToken cancellationToken)
        => Ok(await tenants.UpdateWhatsAppNumberForCurrentTenantAsync(request, cancellationToken));

    /// <summary>This month's usage charges - WhatsApp sending plus lead discovery. Estimates from
    /// hand-maintained rate tables, not an invoice; see ITenantChargesService's own doc comment.</summary>
    [HttpGet("charges")]
    public async Task<ActionResult<TenantChargesDto>> GetCharges(CancellationToken cancellationToken)
        => Ok(await _charges.GetCurrentMonthAsync(cancellationToken));

    /// <summary>The tenant's Admin saves its own WhatsApp credentials (shared with the Platform Admin Console, which
    /// writes the same row). Saving clears any earlier verification: verify again afterwards. Same "null keeps the
    /// stored secret, empty string clears it" convention as the platform endpoint.</summary>
    [HttpPut("whatsapp")]
    public async Task<ActionResult<TenantWhatsAppConfigDto>> SaveWhatsAppConfig(
        [FromBody] UpdateTenantWhatsAppConfigRequest request,
        [FromServices] ICurrentUserService currentUser,
        [FromServices] IPlatformJobService jobs,
        [FromServices] ILogger<TenantSettingsController> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var result = await _whatsAppConfigProvider.SaveConfigForTenantAsync(tenantId, request, currentUser.UserId, cancellationToken);

        // A token pasted from Meta's explorer lives about an hour; exchange it now rather than at the nightly run,
        // exactly as the platform endpoint does. Never fails the save.
        if ((!string.IsNullOrEmpty(request.AccessToken) || !string.IsNullOrEmpty(request.AppSecret)) && currentUser.UserId is { } actor)
        {
            try
            {
                await jobs.TriggerAsync(tenantId, TenantJobTypes.WhatsAppTokenRefresh, actor, currentUser.Email ?? string.Empty, cancellationToken);
            }
            catch (Exception ex) when (ex is ConflictException or NotFoundException)
            {
                logger.LogWarning("WhatsApp token refresh not started after a tenant saved credentials for {TenantId}: {Reason}", tenantId, ex.Message);
            }
        }

        return Ok(result);
    }

    /// <summary>Asks Meta whether the saved credentials reach the phone number, and records the answer.</summary>
    [HttpPost("whatsapp/verify")]
    public async Task<ActionResult<TenantWhatsAppConfigDto>> VerifyWhatsAppConfig(
        [FromServices] ITenantWhatsAppConnectionVerifier verifier, CancellationToken cancellationToken)
        => Ok(await verifier.VerifyAsync(RequireTenantId(), cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated tenant-settings request has no tenant in scope.");
}
