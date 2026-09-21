using System.Text;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Webhooks;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Meta calls INTO this controller - the inverse of the outbound IWhatsAppService client. GET is the
/// one-time verification handshake Meta performs when a webhook URL is registered; POST is every
/// subsequent inbound delivery (messages and status updates). Anonymous by design: Meta cannot supply
/// a bearer token, and POST's real gate is the HMAC signature check against the raw body, not JWT auth.
///
/// All tenants share one platform Meta App under BYO-WABA (Meta subscribes per-App, not per-WABA), so
/// there is exactly one webhook URL for every tenant - POST resolves *which* tenant a delivery belongs
/// to itself, off Meta's own metadata.phone_number_id, before trusting anything else about it.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/whatsapp")]
[AllowAnonymous]
// Deliberately the loosest of the three budgets - see RateLimitOptions.Webhook. A 429 returned to
// Meta is a message this platform never receives, so the limit here exists to bound an abusive
// caller hammering a public URL, not to shape legitimate delivery traffic.
[EnableRateLimiting(RateLimitingServiceExtensions.WebhookPolicy)]
public class WebhooksController : ControllerBase
{
    private readonly IInboundWebhookProcessor _processor;
    private readonly IWhatsAppWebhookParser _parser;
    private readonly IWebhookSignatureValidator _signatureValidator;
    private readonly ITenantWhatsAppConfigProvider _tenantConfigProvider;
    private readonly WhatsAppSettings _settings;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IInboundWebhookProcessor processor,
        IWhatsAppWebhookParser parser,
        IWebhookSignatureValidator signatureValidator,
        ITenantWhatsAppConfigProvider tenantConfigProvider,
        IOptionsSnapshot<WhatsAppSettings> settings,
        IBackgroundJobClient backgroundJobClient,
        ILogger<WebhooksController> logger)
    {
        _processor = processor;
        _parser = parser;
        _signatureValidator = signatureValidator;
        _tenantConfigProvider = tenantConfigProvider;
        _settings = settings.Value;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    /// <summary>Meta's one-time verification handshake, performed when the webhook URL is registered
    /// in the Meta App dashboard. Deliberately still checks one platform-global
    /// WhatsAppSettings.WebhookVerifyToken, not anything per-tenant: Meta subscribes this handshake
    /// once per App, not once per WABA/tenant, and every tenant shares the one platform Meta App under
    /// BYO-WABA - see TenantWhatsAppConfig.WebhookVerifyToken's own doc comment for why that field
    /// exists on the entity anyway (schema completeness / a possible future per-tenant handshake) but
    /// is not what gets checked here.</summary>
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

        // Parse first (cheap, never throws - see WhatsAppWebhookParser's own doc comment) so the
        // tenant-routing key (metadata.phone_number_id) is available before anything else happens.
        var parsed = _parser.Parse(rawBody);
        if (string.IsNullOrEmpty(parsed.PhoneNumberId))
        {
            _logger.LogWarning("Rejected WhatsApp webhook POST: no metadata.phone_number_id in payload");
            return Ok(); // Not this system's problem to retry - Meta will not resend a payload it reshapes.
        }

        var lookup = await _tenantConfigProvider.GetByPhoneNumberIdAsync(parsed.PhoneNumberId, cancellationToken);
        if (lookup is null)
        {
            // No tenant has connected this phone number - either a stale/misdirected delivery, or a
            // tenant mid-disconnect. A 404 (rather than 401) here is deliberately not "signature
            // invalid", it is "nobody to check a signature against" - Meta treats both as a delivery
            // failure and retries on its own schedule regardless of which one this was.
            _logger.LogWarning("Rejected WhatsApp webhook POST: no tenant connected for phone_number_id {PhoneNumberId}", parsed.PhoneNumberId);
            return NotFound();
        }

        var signatureHeader = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!_signatureValidator.IsValid(Encoding.UTF8.GetBytes(rawBody), signatureHeader, lookup.Credentials.AppSecret))
        {
            _logger.LogWarning("Rejected WhatsApp webhook POST: signature verification failed for tenant {TenantId}", lookup.TenantId);
            return Unauthorized();
        }

        // RecordAsync itself calls ITenantContext.SetTenant(lookup.TenantId) before touching anything
        // tenant-owned - see IInboundWebhookProcessor.RecordAsync's own doc comment.
        var webhookEventId = await _processor.RecordAsync(lookup.TenantId, "whatsapp_webhook", rawBody, cancellationToken);

        // Enqueued, not processed inline: Meta expects a fast 2xx and redelivers on a slow or
        // non-2xx response, and actual processing (customer creation, conversation lookup, handoff
        // creation, SignalR push) is exactly the kind of work that should not hold up the response
        // Meta is waiting on. tenantId is threaded through explicitly - this job gets its own DI scope
        // with no ambient tenant to inherit.
        _backgroundJobClient.Enqueue<InboundWebhookProcessingJob>(job => job.RunAsync(lookup.TenantId, webhookEventId));

        return Ok();
    }
}
