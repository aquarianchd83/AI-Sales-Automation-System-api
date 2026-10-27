using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's QR-bridge pages - PlatformSuperAdmin-only, like every other WhatsApp credential in this app. The bridge key
/// is masked on read; a QR code is returned only to the caller who asked for it and is never stored or logged.</summary>
[ApiController]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformWhatsAppBridgeController : ControllerBase
{
    private readonly IWhatsAppBridgeSettingsService _settings;
    private readonly IWhatsAppChannelService _channels;
    private readonly IPlatformAuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public PlatformWhatsAppBridgeController(
        IWhatsAppBridgeSettingsService settings, IWhatsAppChannelService channels, IPlatformAuditService audit, ICurrentUserService currentUser)
    {
        _settings = settings;
        _channels = channels;
        _audit = audit;
        _currentUser = currentUser;
    }

    [HttpGet("api/v1/platform/whatsapp-bridge-settings")]
    public async Task<ActionResult<WhatsAppBridgeSettingsDto>> GetSettings(CancellationToken cancellationToken)
        => Ok(await _settings.GetAsync(cancellationToken));

    /// <summary>Checks the bridge answers with the key before saving; a rejected or unreachable bridge leaves the stored settings untouched (400).</summary>
    [HttpPut("api/v1/platform/whatsapp-bridge-settings")]
    public async Task<ActionResult<WhatsAppBridgeSettingsDto>> UpdateSettings([FromBody] UpdateWhatsAppBridgeSettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _settings.UpdateAsync(request, _currentUser.UserId, cancellationToken);
        await LogAsync(PlatformAuditActions.WhatsAppBridgeSettingsUpdated,
            $"Bridge {(result.IsConfigured ? "configured" : "not configured")} ({result.BaseUrl}); key {(request.ApiKey is null ? "unchanged" : "updated")}; pilot tenants: {result.PilotTenantIds.Count}",
            cancellationToken);
        return Ok(result);
    }

    [HttpGet("api/v1/platform/tenants/{tenantId:guid}/whatsapp-bridge")]
    public async Task<ActionResult<WhatsAppChannelStatusDto>> GetStatus(Guid tenantId, CancellationToken cancellationToken)
        => await Bridge(() => _channels.GetStatusAsync(tenantId, cancellationToken));

    /// <summary>Starts or resumes pairing and returns a QR. Poll GET for the status: connected only once the bridge itself says so.</summary>
    [HttpPost("api/v1/platform/tenants/{tenantId:guid}/whatsapp-bridge/connect")]
    public async Task<ActionResult<WhatsAppChannelStatusDto>> Connect(Guid tenantId, CancellationToken cancellationToken)
    {
        var result = await Bridge(() => _channels.ConnectAsync(tenantId, cancellationToken));
        await LogAsync(PlatformAuditActions.WhatsAppChannelConnectRequested, $"QR connection requested for tenant {tenantId}", cancellationToken);
        return result;
    }

    /// <summary><c>confirmed: false</c> means the bridge still reports the session open - tell the user to remove the linked device on the phone.</summary>
    [HttpPost("api/v1/platform/tenants/{tenantId:guid}/whatsapp-bridge/disconnect")]
    public async Task<ActionResult<WhatsAppChannelStatusDto>> Disconnect(Guid tenantId, CancellationToken cancellationToken)
    {
        var result = await Bridge(() => _channels.DisconnectAsync(tenantId, cancellationToken));
        await LogAsync(PlatformAuditActions.WhatsAppChannelDisconnected, $"QR disconnect requested for tenant {tenantId}", cancellationToken);
        return result;
    }

    [HttpDelete("api/v1/platform/tenants/{tenantId:guid}/whatsapp-bridge")]
    public async Task<ActionResult<WhatsAppChannelStatusDto>> Remove(Guid tenantId, CancellationToken cancellationToken)
    {
        var result = await Bridge(() => _channels.RemoveAsync(tenantId, cancellationToken));
        await LogAsync(PlatformAuditActions.WhatsAppChannelRemoved, $"QR channel removal requested for tenant {tenantId}", cancellationToken);
        return result;
    }

    /// <summary>A bridge that cannot be reached or refuses a call is a 502 with a message an operator can act on, not a 500.</summary>
    private async Task<ActionResult<WhatsAppChannelStatusDto>> Bridge(Func<Task<WhatsAppChannelStatusDto>> call)
    {
        try
        {
            return Ok(await call());
        }
        catch (WhatsAppBridgeException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails { Title = "WhatsApp bridge error", Detail = ex.Message, Status = StatusCodes.Status502BadGateway });
        }
    }

    private Task LogAsync(string action, string details, CancellationToken cancellationToken) =>
        _audit.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty,
            action, details: details, cancellationToken: cancellationToken);
}
