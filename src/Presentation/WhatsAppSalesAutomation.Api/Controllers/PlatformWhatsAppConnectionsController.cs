using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's WhatsApp Connections screen (spec item #5) -
/// PlatformSuperAdmin-only.</summary>
[ApiController]
[Route("api/v1/platform/whatsapp-connections")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformWhatsAppConnectionsController : ControllerBase
{
    private readonly IPlatformWhatsAppConnectionService _connectionService;

    public PlatformWhatsAppConnectionsController(IPlatformWhatsAppConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlatformWhatsAppConnectionDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _connectionService.GetAllAsync(cancellationToken));
}
