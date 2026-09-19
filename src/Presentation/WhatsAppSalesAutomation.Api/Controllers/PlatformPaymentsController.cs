using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Payments screen - PlatformSuperAdmin-only, read-only. Refunds are decided in
/// PlatformRefundsController; nothing here moves money.</summary>
[ApiController]
[Route("api/v1/platform/payments")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformPaymentsController : ControllerBase
{
    private readonly IPlatformPaymentService _payments;

    public PlatformPaymentsController(IPlatformPaymentService payments)
    {
        _payments = payments;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<PlatformPaymentListItemDto>>> GetPaged([FromQuery] PlatformPaymentQuery query, CancellationToken cancellationToken)
        => Ok(await _payments.GetPagedAsync(query, cancellationToken));
}
