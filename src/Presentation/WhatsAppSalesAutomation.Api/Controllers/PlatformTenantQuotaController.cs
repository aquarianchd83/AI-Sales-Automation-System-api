using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>A tenant's prepaid quota from the Platform Admin Console - PlatformSuperAdmin-only. Viewing is
/// free; every adjustment needs a reason and is written to both the quota ledger and the audit log.</summary>
[ApiController]
[Route("api/v1/platform/tenants/{tenantId:guid}/quota")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformTenantQuotaController : ControllerBase
{
    private readonly IQuotaLedgerService _quota;
    private readonly IAiUsageService _aiUsage;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    private readonly ITenantNotifier _notifier;

    public PlatformTenantQuotaController(IQuotaLedgerService quota, IAiUsageService aiUsage, IPlatformAuditService auditService, ICurrentUserService currentUser, ITenantNotifier notifier)
    {
        _notifier = notifier;
        _quota = quota;
        _aiUsage = aiUsage;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<QuotaBalanceDto>>> GetBalances(Guid tenantId, CancellationToken cancellationToken)
        => Ok(await _quota.GetBalancesAsync(tenantId, cancellationToken));

    [HttpGet("ledger")]
    public async Task<ActionResult<PagedResult<QuotaLedgerEntryDto>>> GetLedger(Guid tenantId, [FromQuery] QuotaLedgerQuery query, CancellationToken cancellationToken)
        => Ok(await _quota.GetLedgerAsync(tenantId, query, cancellationToken));

    [HttpGet("ai-usage")]
    public async Task<ActionResult<PagedResult<AiTransactionDto>>> GetAiUsage(Guid tenantId, [FromQuery] AiTransactionQuery query, CancellationToken cancellationToken)
        => Ok(await _aiUsage.GetHistoryAsync(tenantId, query, cancellationToken));

    /// <summary>Positive adds a grant (valid <c>validForDays</c>, default a year); negative removes from what
    /// the tenant has left. <c>reason</c> is mandatory.</summary>
    [HttpPost("adjust")]
    public async Task<IActionResult> Adjust(Guid tenantId, [FromBody] AdjustQuotaRequest request, CancellationToken cancellationToken)
    {
        var actor = _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");
        await _quota.AdjustAsync(tenantId, request.QuotaType, request.UnitsDelta, request.Reason, actor, request.ValidForDays ?? 365, cancellationToken);

        await _auditService.LogAsync(
            actor, _currentUser.Email ?? string.Empty, PlatformAuditActions.TenantQuotaAdjusted, tenantId,
            details: $"{request.QuotaType} {request.UnitsDelta:+0.####;-0.####} - {request.Reason}", cancellationToken: cancellationToken);

        // Only additions are good news worth telling the tenant about; a removal is an operator correction.
        if (request.UnitsDelta > 0)
        {
            var label = QuotaAlertService.Label(request.QuotaType);
            var units = request.UnitsDelta.ToString("#,##0.##");
            var validDays = request.ValidForDays ?? 365;
            await _notifier.NotifyAsync(
                new TenantNotificationRequest(
                    tenantId, TenantNotificationKind.CreditsAdded, request.QuotaType, Guid.NewGuid().ToString("N"),
                    $"{units} {label} added",
                    $"{units} {label} have been added to your account. They are valid for {validDays} days.",
                    AlsoWhatsApp: false),
                cancellationToken);
        }

        return Ok(await _quota.GetBalancesAsync(tenantId, cancellationToken));
    }
}

public record AdjustQuotaRequest(QuotaType QuotaType, decimal UnitsDelta, string Reason, int? ValidForDays = null);
