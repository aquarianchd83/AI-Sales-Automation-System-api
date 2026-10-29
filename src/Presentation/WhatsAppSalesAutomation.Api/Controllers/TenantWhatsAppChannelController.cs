using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The tenant Admin's side of the WhatsApp page: which connection style the Platform Admin chose for this tenant (Meta Cloud API or QR code),
/// and - only while that is QR - pairing the tenant's own number. A tenant can read the choice but never change it.</summary>
[ApiController]
[Route("api/v1/tenant-settings")]
[Authorize(Roles = AppRoles.Admin)]
public class TenantWhatsAppChannelController : ControllerBase
{
    private readonly IWhatsAppProviderSettingsService _providers;
    private readonly IWhatsAppChannelService _channels;
    private readonly ITenantContext _tenantContext;

    public TenantWhatsAppChannelController(IWhatsAppProviderSettingsService providers, IWhatsAppChannelService channels, ITenantContext tenantContext)
    {
        _providers = providers;
        _channels = channels;
        _tenantContext = tenantContext;
    }

    [HttpGet("whatsapp-provider")]
    public async Task<ActionResult<TenantWhatsAppProviderDto>> GetProvider(CancellationToken cancellationToken)
        => Ok(await _providers.GetForTenantAsync(RequireTenantId(), cancellationToken));

    [HttpGet("whatsapp-bridge")]
    public Task<ActionResult<WhatsAppChannelStatusDto>> GetStatus(CancellationToken cancellationToken)
        => Bridge(id => _channels.GetStatusAsync(id, cancellationToken), requireQr: false);

    [HttpPost("whatsapp-bridge/connect")]
    public Task<ActionResult<WhatsAppChannelStatusDto>> Connect(CancellationToken cancellationToken)
        => Bridge(id => _channels.ConnectAsync(id, cancellationToken), requireQr: true);

    [HttpPost("whatsapp-bridge/disconnect")]
    public Task<ActionResult<WhatsAppChannelStatusDto>> Disconnect(CancellationToken cancellationToken)
        => Bridge(id => _channels.DisconnectAsync(id, cancellationToken), requireQr: false);

    [HttpDelete("whatsapp-bridge")]
    public Task<ActionResult<WhatsAppChannelStatusDto>> Remove(CancellationToken cancellationToken)
        => Bridge(id => _channels.RemoveAsync(id, cancellationToken), requireQr: false);

    private async Task<ActionResult<WhatsAppChannelStatusDto>> Bridge(Func<Guid, Task<WhatsAppChannelStatusDto>> call, bool requireQr)
    {
        var tenantId = RequireTenantId();
        if (requireQr && (await _providers.GetForTenantAsync(tenantId)).Effective != WhatsAppProviders.QrBridge)
            return Conflict(new ProblemDetails { Title = "QR connection is not enabled", Detail = "Your platform administrator has not enabled the QR-code connection for your account.", Status = StatusCodes.Status409Conflict });

        try
        {
            return Ok(await call(tenantId));
        }
        catch (WhatsAppBridgeException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails { Title = "WhatsApp bridge error", Detail = ex.Message, Status = StatusCodes.Status502BadGateway });
        }
    }

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("No tenant in scope.");
}
