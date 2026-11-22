using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Wallet;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// A tenant topping up its credit wallet through Razorpay. Admin-only, like the rest of billing. The tenant is read from the caller's token - there is
/// no tenant id in any request - and every lookup is scoped to it, so one tenant can never read or settle another's transactions. The wallet is only
/// credited by the server, after it has verified the payment; the browser's "success" callback proves nothing by itself.
/// </summary>
[ApiController]
[Route("api/v1/wallet")]
[Authorize(Roles = AppRoles.Admin)]
public class WalletRechargeController : ControllerBase
{
    private readonly IWalletRechargeService _recharge;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public WalletRechargeController(IWalletRechargeService recharge, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _recharge = recharge;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    /// <summary>The wallet balance in credits.</summary>
    [HttpGet("balance")]
    public async Task<ActionResult<WalletBalanceDto>> GetBalance(CancellationToken cancellationToken)
        => Ok(await _recharge.GetBalanceAsync(RequireTenantId(), cancellationToken));

    /// <summary>The quick-pick amounts, the min/max limits, the credits-per-rupee rate and the balance - what the recharge screen starts from.</summary>
    [HttpGet("recharge/options")]
    public async Task<ActionResult<RechargeOptionsDto>> GetOptions(CancellationToken cancellationToken)
        => Ok(await _recharge.GetOptionsAsync(RequireTenantId(), cancellationToken));

    /// <summary>What an amount costs: tax on top, the total payable, the credits it buys and the balance after. Creates nothing.</summary>
    [HttpGet("recharge/quote")]
    public async Task<ActionResult<RechargeQuoteDto>> Quote([FromQuery] decimal amount, CancellationToken cancellationToken)
        => Ok(await _recharge.QuoteAsync(RequireTenantId(), amount, cancellationToken));

    [HttpPost("recharge/create-order")]
    public async Task<ActionResult<RechargeCheckoutDto>> CreateOrder([FromBody] CreateRechargeOrderRequest request, CancellationToken cancellationToken)
        => Ok(await _recharge.CreateOrderAsync(RequireTenantId(), _currentUser.UserId, request, cancellationToken));

    /// <summary>A Razorpay order for one credit pack (the "Buy" buttons). Verified and credited through the same verify-payment / webhook / reconciliation path.</summary>
    [HttpPost("recharge/credit-packs/{packId:guid}/create-order")]
    public async Task<ActionResult<RechargeCheckoutDto>> CreatePackOrder(Guid packId, CancellationToken cancellationToken)
        => Ok(await _recharge.CreatePackOrderAsync(RequireTenantId(), _currentUser.UserId, packId, cancellationToken));

    [HttpPost("recharge/verify-payment")]
    public async Task<ActionResult<RechargeVerifyResultDto>> VerifyPayment([FromBody] VerifyRechargePaymentRequest request, CancellationToken cancellationToken)
        => Ok(await _recharge.VerifyAsync(RequireTenantId(), request, cancellationToken));

    [HttpGet("recharge/payment-status/{transactionId:guid}")]
    public async Task<ActionResult<RechargeTransactionDto>> GetPaymentStatus(Guid transactionId, CancellationToken cancellationToken)
        => Ok(await _recharge.GetStatusAsync(RequireTenantId(), transactionId, cancellationToken));

    [HttpGet("recharge/history")]
    public async Task<ActionResult<PagedResult<RechargeTransactionDto>>> GetHistory([FromQuery] PagedRequest paging, CancellationToken cancellationToken)
        => Ok(await _recharge.GetHistoryAsync(RequireTenantId(), paging, cancellationToken));

    /// <summary>Admin guarantees a real tenant is in scope; this gives that guarantee a non-nullable type.</summary>
    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated wallet request has no tenant in scope.");
}
