using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Admin UI for the six DB-backed config sections (WhatsApp, AiProviders, Campaigns, Media,
/// Messaging, Ai - see AppSettingCatalog) that used to only be editable by hand-editing
/// appsettings.json and restarting the app. SuperAdmin-only: WhatsApp/AiProviders hold real
/// credentials, and every other section here still tunes live production behaviour.
/// </summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize(Roles = AppRoles.SuperAdmin)]
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
