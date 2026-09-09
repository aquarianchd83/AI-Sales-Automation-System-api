using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using WhatsAppSalesAutomation.Infrastructure.Billing;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Stripe calls INTO this controller - a separate route and a separate signature scheme from
/// WebhooksController (the WhatsApp one): Stripe signs with its own Stripe-Signature header/HMAC
/// construction (Stripe.EventUtility, not WebhookSignatureValidator's raw HMAC), and there is no GET
/// verification handshake the way Meta has - Stripe verifies a webhook endpoint by simply delivering
/// to it and checking the response code, so this controller only ever needs POST. Anonymous by
/// design, same reasoning as the WhatsApp webhook: Stripe cannot supply a bearer token, and the real
/// gate is the signature check against the raw body.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/stripe")]
[AllowAnonymous]
public class StripeWebhooksController : ControllerBase
{
    private readonly IStripeWebhookHandler _handler;
    private readonly ILogger<StripeWebhooksController> _logger;

    public StripeWebhooksController(IStripeWebhookHandler handler, ILogger<StripeWebhooksController> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(cancellationToken);
        var signatureHeader = Request.Headers["Stripe-Signature"].FirstOrDefault();

        try
        {
            await _handler.HandleEventAsync(json, signatureHeader ?? string.Empty, cancellationToken);
        }
        catch (StripeException ex)
        {
            // Covers both "signature didn't verify" and "malformed payload" - either way this is not
            // a real Stripe delivery worth Stripe retrying, so 400 (not 401/500) tells Stripe's own
            // dashboard to flag it rather than hammer the endpoint again.
            _logger.LogWarning(ex, "Rejected Stripe webhook POST");
            return BadRequest();
        }

        return Ok();
    }
}
