using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The platform admin's wallet of money loaded into the AI providers: one balance, and the history of every top-up. PlatformSuperAdmin-only;
/// every top-up is written to the audit log.</summary>
[ApiController]
[Route("api/v1/platform/wallet")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformWalletController : ControllerBase
{
    private readonly IPlatformWalletService _wallet;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformWalletController(IPlatformWalletService wallet, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _wallet = wallet;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<PlatformWalletSummaryDto>> GetSummary(CancellationToken cancellationToken)
        => Ok(await _wallet.GetSummaryAsync(cancellationToken));

    [HttpGet("top-ups")]
    public async Task<ActionResult<PagedResult<WalletTopUpDto>>> GetTopUps([FromQuery] PagedRequest query, CancellationToken cancellationToken)
        => Ok(await _wallet.GetTopUpsAsync(query, cancellationToken));

    [HttpPost("top-ups")]
    public async Task<ActionResult<WalletTopUpDto>> AddTopUp([FromBody] AddWalletTopUpRequest request, CancellationToken cancellationToken)
    {
        var actor = _currentUser.UserId ?? Guid.Empty;
        var added = await _wallet.AddTopUpAsync(request, _currentUser.UserId, cancellationToken);

        await _auditService.LogAsync(
            actor, _currentUser.Email ?? string.Empty, PlatformAuditActions.PlatformWalletToppedUp, null,
            details: $"{added.Provider} INR {added.Amount:0.##} on {added.Date:yyyy-MM-dd}", cancellationToken: cancellationToken);

        return Ok(added);
    }
}
