using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Admin UI for the six DB-backed config sections (WhatsApp, AiProviders, Campaigns, Media,
/// Messaging, Ai - see AppSettingCatalog) that used to only be editable by hand-editing
/// appsettings.json and restarting the app.
///
/// PlatformSuperAdmin-only, not tenant SuperAdmin: since the SaaS conversion, per-tenant WhatsApp/AI
/// credentials live on each tenant's own /tenant-settings endpoints (see TenantSettingsController) -
/// what's left here is purely platform-owned config (the platform's own fallback AI provider keys,
/// default plan config, etc.), shared by every tenant. Letting a tenant's SuperAdmin edit that would
/// let one tenant change behaviour/credentials for every other tenant on the platform.
/// </summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;
    private readonly ICurrentUserService _currentUser;
    private readonly IAppSettingsReloader _reloader;

    public SettingsController(ISettingsService settingsService, ICurrentUserService currentUser, IAppSettingsReloader reloader)
    {
        _settingsService = settingsService;
        _currentUser = currentUser;
        _reloader = reloader;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SettingCategoryDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _settingsService.GetAllAsync(cancellationToken));

    /// <summary>Tries each saved AI provider key (Anthropic, OpenAI, Google), or only the one named by ?provider=, by listing the provider's models - no tokens
    /// spent. Uses what is SAVED, so save first. Always 200: each result says whether its key worked.</summary>
    [HttpPost("ai-providers/verify")]
    public async Task<ActionResult<IReadOnlyList<AiProviderCheckDto>>> VerifyAiProviders(
        [FromServices] IAiProviderVerifier verifier, [FromServices] IPlatformAiProvidersService insights, [FromQuery] string? provider, CancellationToken cancellationToken)
    {
        var checks = await verifier.VerifyAsync(provider, cancellationToken);
        // Recorded so the screen can say when each provider was last checked, and how - the check itself is unchanged.
        await insights.RecordChecksAsync(checks, _currentUser.UserId, _currentUser.Email ?? string.Empty, cancellationToken);
        return Ok(checks);
    }

    [HttpGet("{category}")]
    public async Task<ActionResult<SettingCategoryDto>> GetCategory(string category, CancellationToken cancellationToken)
        => Ok(await _settingsService.GetCategoryAsync(category, cancellationToken));

    /// <summary>Only the keys present in <paramref name="request"/>'s Values dictionary are changed -
    /// see UpdateSettingsRequest's own doc comment. Takes effect immediately for every new
    /// request/job scope, no app restart, except for the Provider/EmbeddingProvider keys (see
    /// AppSettingCatalog).</summary>
    [HttpPut("{category}")]
    public async Task<IActionResult> UpdateCategory(string category, [FromBody] UpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        await _settingsService.UpdateCategoryAsync(category, request, _currentUser.UserId, cancellationToken);

        // AI provider keys, models and switches, and the integration settings, are audited (which keys changed - never their values).
        if (string.Equals(category, "AiProviders", StringComparison.OrdinalIgnoreCase) || string.Equals(category, "Integrations", StringComparison.OrdinalIgnoreCase))
        {
            var insights = HttpContext.RequestServices.GetRequiredService<IPlatformAiProvidersService>();
            await insights.AuditSettingsChangeAsync(category, request.Values.Keys.ToList(), _currentUser.UserId, _currentUser.Email ?? string.Empty, cancellationToken);
        }

        return NoContent();
    }

    /// <summary>Safety valve, not part of the normal save flow (UpdateCategory already reloads on
    /// every save) - re-syncs the live config from the DB on demand, e.g. after a row was edited
    /// directly in the database rather than through this API.</summary>
    [HttpPost("reload")]
    public IActionResult Reload()
    {
        _reloader.Reload();
        return NoContent();
    }
}
