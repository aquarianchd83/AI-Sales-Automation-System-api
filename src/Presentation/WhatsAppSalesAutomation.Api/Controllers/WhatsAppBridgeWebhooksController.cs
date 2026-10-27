using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Application.Webhooks;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The QR bridge calls INTO this controller. Separate from the Meta endpoint on purpose: different authentication (no HMAC is offered), a different
/// payload, and a tenant that comes from the URL, not from anything in the body. A delivery is accepted only when ALL of these hold:
/// the unguessable path token and the secret header both match this channel's stored hashes (which is what names the tenant), and the instance
/// token the bridge puts in the body matches the one stored for that channel. A failure on any of them answers the same 401.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/whatsapp-bridge")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingServiceExtensions.WebhookPolicy)]
public class WhatsAppBridgeWebhooksController : ControllerBase
{
    private readonly IWhatsAppChannelStore _channels;
    private readonly IWhatsAppChannelService _channelService;
    private readonly IInboundWebhookProcessor _processor;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<WhatsAppBridgeWebhooksController> _logger;

    public WhatsAppBridgeWebhooksController(
        IWhatsAppChannelStore channels, IWhatsAppChannelService channelService, IInboundWebhookProcessor processor,
        IBackgroundJobClient jobs, ILogger<WhatsAppBridgeWebhooksController> logger)
    {
        _channels = channels;
        _channelService = channelService;
        _processor = processor;
        _jobs = jobs;
        _logger = logger;
    }

    [HttpPost("{channelToken}")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Receive(string channelToken, CancellationToken cancellationToken)
    {
        var secret = Request.Headers[WhatsAppChannelService.SecretHeaderName].FirstOrDefault();
        var channel = await _channels.FindByWebhookAsync(channelToken, secret ?? string.Empty, cancellationToken);
        if (channel is null)
        {
            _logger.LogWarning("Rejected WhatsApp bridge webhook: unknown channel or bad secret");
            return Unauthorized();
        }

        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync(cancellationToken);

        var evt = EvolutionWebhookNormalizer.Normalize(rawBody);

        // The body's own token is a second proof the delivery came from this tenant's instance.
        if (evt.ProvidedToken is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(evt.ProvidedToken), Encoding.UTF8.GetBytes(channel.InstanceToken)))
        {
            _logger.LogWarning("Rejected WhatsApp bridge webhook for tenant {TenantId}: instance token mismatch", channel.TenantId);
            return Unauthorized();
        }

        switch (evt.Kind)
        {
            case NormalizedBridgeEvent.EventKind.Connection:
                await _channelService.ApplyConnectionUpdateAsync(channel.TenantId, evt.ConnectionState, evt.ConnectedPhone, cancellationToken);
                break;

            case NormalizedBridgeEvent.EventKind.Messages:
                // Only the secret-free canonical form is stored, never the raw body (which carries the instance token).
                var webhookEventId = await _processor.RecordAsync(channel.TenantId, "whatsapp_bridge_webhook", evt.Canonical!, cancellationToken);
                _jobs.Enqueue<InboundWebhookProcessingJob>(job => job.RunAsync(channel.TenantId, webhookEventId));
                break;
        }

        return Ok();
    }
}
