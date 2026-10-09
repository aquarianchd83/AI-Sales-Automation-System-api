using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.MetaOnboarding;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// A tenant's own settings: its WhatsApp Business Account connection (which the tenant's Admin and the Platform
/// Admin both own - either can save it and verify it against Meta), and message usage and charges. There is no AI
/// provider setting here: the provider, model and key are platform-wide. Secrets are never returned, only whether each is set.
/// </summary>
[ApiController]
[Route("api/v1/tenant-settings")]
[Authorize(Roles = AppRoles.Admin)]
public class TenantSettingsController : ControllerBase
{
    private readonly ITenantWhatsAppConfigProvider _whatsAppConfigProvider;
    private readonly IPlanLimitsService _planLimits;
    private readonly ITenantChargesService _charges;
    private readonly ITenantContext _tenantContext;

    public TenantSettingsController(
        ITenantWhatsAppConfigProvider whatsAppConfigProvider,
        IPlanLimitsService planLimits,
        ITenantChargesService charges,
        ITenantContext tenantContext)
    {
        _whatsAppConfigProvider = whatsAppConfigProvider;
        _planLimits = planLimits;
        _charges = charges;
        _tenantContext = tenantContext;
    }

    /// <summary>Null (not 404) when the tenant has never configured a WABA - "not connected yet" is a
    /// normal state for a new trial tenant, not a missing resource.</summary>
    [HttpGet("whatsapp")]
    public async Task<ActionResult<TenantWhatsAppConfigDto?>> GetWhatsAppConfig(CancellationToken cancellationToken)
        => Ok(await _whatsAppConfigProvider.GetConfigForCurrentTenantAsync(cancellationToken));

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

    // ---- "Connect with Meta" (WhatsApp Embedded Signup) ----------------------------------------------------------------
    // A Meta-side problem is never an HTTP error here: it comes back 200 with an issue (plain-language title, message and
    // next action) and the steps so far, so the screen can explain it and keep what is already done.

    /// <summary>What the browser needs to open Meta's sign-up popup - or why "Connect with Meta" is not available yet.</summary>
    [HttpGet("whatsapp/meta-signup/config")]
    public async Task<ActionResult<MetaSignupClientConfigDto>> GetMetaSignupConfig(
        [FromServices] IMetaEmbeddedSignupService signup, CancellationToken cancellationToken)
        => Ok(await signup.GetClientConfigAsync(cancellationToken));

    /// <summary>Where each onboarding step stands now, read from Meta with the saved credentials.</summary>
    [HttpGet("whatsapp/meta-signup/status")]
    public async Task<ActionResult<MetaSignupResultDto>> GetMetaSignupStatus(
        [FromServices] IMetaEmbeddedSignupService signup, CancellationToken cancellationToken)
        => Ok(await signup.GetStatusAsync(RequireTenantId(), cancellationToken));

    /// <summary>Finishes the sign-in the browser just completed with Meta: exchanges the code, finds the WhatsApp account and
    /// number, saves the credentials encrypted for this tenant, registers the number, turns on webhooks and reads templates.</summary>
    [HttpPost("whatsapp/meta-signup/complete")]
    public async Task<ActionResult<MetaSignupResultDto>> CompleteMetaSignup(
        [FromBody] CompleteMetaSignupRequest request,
        [FromServices] IMetaEmbeddedSignupService signup,
        [FromServices] ICurrentUserService currentUser,
        CancellationToken cancellationToken)
        => Ok(await signup.CompleteAsync(RequireTenantId(), currentUser.UserId, request, cancellationToken));

    /// <summary>Carries on from the first step that is not done, with the credentials already saved - no new Meta sign-in.</summary>
    [HttpPost("whatsapp/meta-signup/resume")]
    public async Task<ActionResult<MetaSignupResultDto>> ResumeMetaSignup(
        [FromServices] IMetaEmbeddedSignupService signup, CancellationToken cancellationToken)
        => Ok(await signup.ResumeAsync(RequireTenantId(), cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated tenant-settings request has no tenant in scope.");
}
