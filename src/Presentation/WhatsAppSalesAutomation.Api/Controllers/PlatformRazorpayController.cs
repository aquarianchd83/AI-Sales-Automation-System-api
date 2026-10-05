using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The Platform Admin Console's Razorpay test page - PlatformSuperAdmin-only. The platform's Razorpay keys, and real test payments made
/// through Razorpay Checkout to prove the integration end to end. Not connected to tenant billing.</summary>
[ApiController]
[Route("api/v1/platform/razorpay")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformRazorpayController : ControllerBase
{
    private readonly IRazorpayService _razorpay;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformRazorpayController(IRazorpayService razorpay, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _razorpay = razorpay;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    /// <summary>Secrets are masked: only whether one is stored and its last four characters come back.</summary>
    [HttpGet("settings")]
    public async Task<ActionResult<RazorpaySettingsDto>> GetSettings(CancellationToken cancellationToken)
        => Ok(await _razorpay.GetSettingsAsync(cancellationToken));

    /// <summary>A null secret keeps the stored one; an empty string clears it.</summary>
    [HttpPut("settings")]
    public async Task<ActionResult<RazorpaySettingsDto>> UpdateSettings([FromBody] UpdateRazorpaySettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _razorpay.UpdateSettingsAsync(request, _currentUser.UserId, cancellationToken);

        var secrets = new List<string>();
        if (request.KeySecret is not null) secrets.Add("key secret");
        if (request.WebhookSecret is not null) secrets.Add("webhook secret");
        await LogAsync(PlatformAuditActions.RazorpaySettingsUpdated,
            $"Razorpay key id '{result.KeyId}' ({result.Mode}); {(secrets.Count == 0 ? "secrets unchanged" : string.Join(" and ", secrets) + " updated")}",
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Checks a key pair against Razorpay, saved or not. Always 200: the result says whether it worked.</summary>
    [HttpPost("settings/test")]
    public async Task<ActionResult<RazorpayCheckResultDto>> TestKeys([FromBody] TestRazorpayKeysRequest request, CancellationToken cancellationToken)
        => Ok(await _razorpay.TestKeysAsync(request, cancellationToken));

    [HttpGet("orders")]
    public async Task<ActionResult<IReadOnlyList<RazorpayOrderDto>>> GetOrders(CancellationToken cancellationToken)
        => Ok(await _razorpay.GetOrdersAsync(cancellationToken));

    /// <summary>Creates a Razorpay order and returns what Checkout needs to open in the browser.</summary>
    [HttpPost("orders")]
    public async Task<ActionResult<RazorpayCheckoutDto>> CreateOrder([FromBody] CreateRazorpayOrderRequest request, CancellationToken cancellationToken)
        => Ok(await _razorpay.CreateOrderAsync(request, _currentUser.UserId, cancellationToken));

    /// <summary>Checkout's success response, checked on the server. Always 200: the result says whether the payment is proven.</summary>
    [HttpPost("orders/{id:guid}/verify")]
    public async Task<ActionResult<RazorpayVerifyResultDto>> Verify(Guid id, [FromBody] VerifyRazorpayPaymentRequest request, CancellationToken cancellationToken)
        => Ok(await _razorpay.VerifyAsync(id, request, cancellationToken));

    [HttpPost("orders/{id:guid}/failed")]
    public async Task<ActionResult<RazorpayOrderDto>> ReportFailure(Guid id, [FromBody] ReportRazorpayFailureRequest request, CancellationToken cancellationToken)
        => Ok(await _razorpay.ReportFailureAsync(id, request, cancellationToken));

    [HttpPost("orders/{id:guid}/refund")]
    public async Task<ActionResult<RazorpayOrderDto>> Refund(Guid id, [FromBody] RefundRazorpayOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await _razorpay.RefundAsync(id, request, cancellationToken);
        await LogAsync(PlatformAuditActions.RazorpayRefundIssued,
            $"Refunded {RazorpayService.Format(result.RefundedMinor, result.Currency)} in all on test order {result.OrderId} ({result.Mode})", cancellationToken);
        return Ok(result);
    }

    [HttpGet("webhook-events")]
    public async Task<ActionResult<IReadOnlyList<RazorpayWebhookEventDto>>> GetWebhookEvents(CancellationToken cancellationToken)
        => Ok(await _razorpay.GetWebhookEventsAsync(cancellationToken));

    private Task LogAsync(string action, string details, CancellationToken cancellationToken) =>
        _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, action,
            details: details, cancellationToken: cancellationToken);
}
