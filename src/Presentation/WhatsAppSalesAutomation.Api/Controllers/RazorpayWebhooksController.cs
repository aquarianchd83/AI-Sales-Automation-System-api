using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Application.Razorpay;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Razorpay calls INTO this endpoint (set it as the webhook URL in Razorpay Dashboard > Account &amp; Settings > Webhooks, with the same secret saved
/// on the test page). Anonymous by design - Razorpay cannot send a bearer token; the real gate is the X-Razorpay-Signature HMAC over the raw body.
/// Answers 2xx for anything signed (including a redelivery, so Razorpay stops retrying) and 400 for anything that is not.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/razorpay")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingServiceExtensions.WebhookPolicy)]
public class RazorpayWebhooksController : ControllerBase
{
    /// <summary>Razorpay's payloads are a few KB; anything far larger is not from Razorpay.</summary>
    private const int MaxBodyBytes = 256 * 1024;

    private readonly IRazorpayService _razorpay;
    private readonly ILogger<RazorpayWebhooksController> _logger;

    public RazorpayWebhooksController(IRazorpayService razorpay, ILogger<RazorpayWebhooksController> logger)
    {
        _razorpay = razorpay;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        // The exact bytes received: the signature is over them, not over a re-serialised copy.
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);

        var outcome = await _razorpay.HandleWebhookAsync(
            buffer.ToArray(),
            Request.Headers["X-Razorpay-Signature"].FirstOrDefault(),
            Request.Headers["X-Razorpay-Event-Id"].FirstOrDefault(),
            cancellationToken);

        switch (outcome)
        {
            case RazorpayWebhookOutcome.InvalidSignature:
                _logger.LogWarning("Rejected Razorpay webhook: signature verification failed");
                return BadRequest();
            case RazorpayWebhookOutcome.NotConfigured:
                _logger.LogWarning("Rejected Razorpay webhook: no webhook secret is saved");
                return BadRequest();
            default:
                return Ok();
        }
    }
}
