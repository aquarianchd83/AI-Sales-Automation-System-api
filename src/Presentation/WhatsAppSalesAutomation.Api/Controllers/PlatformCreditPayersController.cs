using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The Platform Admin Console's Credit Payers screen - PlatformSuperAdmin-only. For each scenario that spends credits it shows who
/// pays (the tenant or the platform), lets the admin switch that at any time, keeps the platform's own wallet, and lists every spend with the
/// account that paid it. Every change is written to the audit log.</summary>
[ApiController]
[Route("api/v1/platform/credit-payers")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformCreditPayersController : ControllerBase
{
    private readonly ICreditPayerService _credits;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly ITenantNotifier _notifier;

    public PlatformCreditPayersController(ICreditPayerService credits, IPlatformAuditService auditService, ICurrentUserService currentUser, ITenantNotifier notifier)
    {
        _credits = credits;
        _auditService = auditService;
        _currentUser = currentUser;
        _notifier = notifier;
    }

    [HttpGet("scenarios")]
    public async Task<ActionResult<IReadOnlyList<CreditScenarioDto>>> GetScenarios(CancellationToken cancellationToken)
        => Ok(await _credits.GetScenariosAsync(cancellationToken));

    /// <summary>Switches who pays for a scenario from now on. Spends already made keep the account they were charged to.</summary>
    [HttpPut("scenarios/{scenario}")]
    public async Task<ActionResult<CreditScenarioDto>> SetPayer(CreditScenario scenario, [FromBody] UpdateCreditPayerRequest request, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var before = (await _credits.GetScenariosAsync(cancellationToken)).FirstOrDefault(s => s.Scenario == scenario);
        var updated = await _credits.SetPayerAsync(scenario, request, actor, cancellationToken);

        await _auditService.LogAsync(
            actor, _currentUser.Email ?? string.Empty, PlatformAuditActions.CreditPayerChanged, null,
            details: $"{updated.Title}: {before?.Payer} -> {updated.Payer}, price {before?.UnitsPerAction:0.####} -> {updated.UnitsPerAction:0.####} credit(s) per {updated.UnitLabel}",
            cancellationToken: cancellationToken);

        return Ok(updated);
    }

    [HttpGet("wallet")]
    public async Task<ActionResult<IReadOnlyList<PlatformCreditWalletDto>>> GetWallets(CancellationToken cancellationToken)
        => Ok(await _credits.GetWalletsAsync(cancellationToken));

    [HttpGet("wallet/ledger")]
    public async Task<ActionResult<PagedResult<PlatformCreditEntryDto>>> GetWalletLedger([FromQuery] PlatformCreditEntryQuery query, CancellationToken cancellationToken)
        => Ok(await _credits.GetWalletLedgerAsync(query, cancellationToken));

    /// <summary>Tops up (positive) or corrects (negative) the platform wallet. A reason is mandatory.</summary>
    [HttpPost("wallet/adjust")]
    public async Task<ActionResult<IReadOnlyList<PlatformCreditWalletDto>>> AdjustWallet([FromBody] AdjustPlatformWalletRequest request, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var wallets = await _credits.AdjustWalletAsync(request, actor, cancellationToken);

        await _auditService.LogAsync(
            actor, _currentUser.Email ?? string.Empty, PlatformAuditActions.PlatformWalletAdjusted, null,
            details: $"{request.QuotaType} {request.UnitsDelta:+0.####;-0.####} - {request.Reason}", cancellationToken: cancellationToken);

        return Ok(wallets);
    }

    /// <summary>Every credit spend, newest first, with the account that was charged and, when the platform could not cover it, that the tenant paid instead.</summary>
    [HttpGet("usage")]
    public async Task<ActionResult<PagedResult<CreditUsageDto>>> GetUsage([FromQuery] CreditUsageQuery query, CancellationToken cancellationToken)
        => Ok(await _credits.GetUsageAsync(query, cancellationToken));

    /// <summary>Gives a tenant back what a spend charged it. The tenant is told.</summary>
    [HttpPost("usage/{usageId:guid}/redeem")]
    public async Task<ActionResult<CreditUsageDto>> Redeem(Guid usageId, [FromBody] RedeemCreditUsageRequest request, CancellationToken cancellationToken)
    {
        var actor = Actor();
        var usage = await _credits.RedeemToTenantAsync(usageId, request.Note, actor, cancellationToken);

        await _auditService.LogAsync(
            actor, _currentUser.Email ?? string.Empty, PlatformAuditActions.CreditUsageRedeemed, usage.TenantId,
            details: $"{usage.Units:0.####} {usage.QuotaType} returned for {usage.ScenarioTitle} - {request.Note}", cancellationToken: cancellationToken);

        var label = QuotaAlertService.Label(usage.QuotaType);
        var units = usage.Units.ToString("#,##0.##");
        await _notifier.NotifyAsync(
            new TenantNotificationRequest(
                usage.TenantId, TenantNotificationKind.CreditsAdded, usage.QuotaType, Guid.NewGuid().ToString("N"),
                $"{units} {label} returned",
                $"{units} {label} have been returned to your account for \"{usage.ScenarioTitle}\". They are valid for 365 days.",
                AlsoWhatsApp: false),
            cancellationToken);

        return Ok(usage);
    }

    private Guid Actor() => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");
}
