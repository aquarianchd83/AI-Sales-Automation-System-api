using System.Text;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Webhooks;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Meta calls INTO this controller - the inverse of the outbound IWhatsAppService client. GET is the
/// one-time verification handshake Meta performs when a webhook URL is registered; POST is every
/// subsequent inbound delivery (messages and status updates). Anonymous by design: Meta cannot supply
/// a bearer token, and POST's real gate is the HMAC signature check against the raw body, not JWT auth.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/whatsapp")]
[AllowAnonymous]
public class WebhooksController : ControllerBase
{
    private readonly IInboundWebhookProcessor _processor;
    private readonly IWebhookSignatureValidator _signatureValidator;
    private readonly WhatsAppSettings _settings;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IInboundWebhookProcessor processor,
        IWebhookSignatureValidator signatureValidator,
        IOptions<WhatsAppSettings> settings,
        IBackgroundJobClient backgroundJobClient,
        ILogger<WebhooksController> logger)
    {
        _processor = processor;
        _signatureValidator = signatureValidator;
        _settings = settings.Value;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    /// <summary>Meta's one-time verification handshake, performed when the webhook URL is registered
    /// in the Meta App dashboard.</summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode != "subscribe" || string.IsNullOrEmpty(_settings.WebhookVerifyToken) || verifyToken != _settings.WebhookVerifyToken)
        {
            _logger.LogWarning("Rejected WhatsApp webhook verification attempt (mode={Mode})", mode);
            return Forbid();
        }

        // Must be the bare challenge string, not wrapped in JSON - Meta compares this response body
        // byte for byte against what it sent.
        return Content(challenge ?? string.Empty, "text/plain");
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        var signatureHeader = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!_signatureValidator.IsValid(Encoding.UTF8.GetBytes(rawBody), signatureHeader))
        {
            _logger.LogWarning("Rejected WhatsApp webhook POST: signature verification failed");
            return Unauthorized();
        }

        var webhookEventId = await _processor.RecordAsync("whatsapp_webhook", rawBody, cancellationToken);

        // Enqueued, not processed inline: Meta expects a fast 2xx and redelivers on a slow or
        // non-2xx response, and actual processing (customer creation, conversation lookup, handoff
        // creation, SignalR push) is exactly the kind of work that should not hold up the response
        // Meta is waiting on.
        _backgroundJobClient.Enqueue<InboundWebhookProcessingJob>(job => job.RunAsync(webhookEventId));

        return Ok();
    }
}
