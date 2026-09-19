using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Invoices screen - PlatformSuperAdmin-only. See
/// IPlatformInvoiceService for what a row is and how it's generated.</summary>
[ApiController]
[Route("api/v1/platform/invoices")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformInvoicesController : ControllerBase
{
    private readonly IPlatformInvoiceService _invoiceService;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformInvoicesController(
        IPlatformInvoiceService invoiceService,
        IPlatformAuditService auditService,
        ICurrentUserService currentUser)
    {
        _invoiceService = invoiceService;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<PlatformInvoiceListItemDto>>> GetPaged(
        [FromQuery] PlatformInvoiceQuery query, CancellationToken cancellationToken)
        => Ok(await _invoiceService.GetPagedAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PlatformInvoiceDetailDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _invoiceService.GetByIdAsync(id, cancellationToken));

    [HttpPost("{id:guid}/mark-paid")]
    public async Task<ActionResult<PlatformInvoiceDetailDto>> MarkPaid(Guid id, CancellationToken cancellationToken)
    {
        var result = await _invoiceService.MarkPaidAsync(id, cancellationToken);

        await _auditService.LogAsync(
            ActorUserId, ActorEmail, PlatformAuditActions.InvoiceMarkedPaid, result.TenantId,
            details: $"Invoice {id} for {result.TenantName} ({result.PeriodStartUtc:yyyy-MM})", cancellationToken: cancellationToken);

        return Ok(result);
    }

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
