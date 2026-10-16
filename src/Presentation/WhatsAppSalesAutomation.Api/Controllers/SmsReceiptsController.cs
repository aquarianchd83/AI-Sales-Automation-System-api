using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The SMS provider calls INTO this controller with delivery receipts for the consent invitations it was given. Anonymous by design -
/// the provider holds no bearer token - so the gate is a shared secret in the X-Webhook-Secret header, compared in constant time. With
/// no secret configured (Sms:ReceiptSecret) the endpoint refuses everything rather than accepting unauthenticated receipts. Idempotent:
/// a repeated receipt changes nothing.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/sms")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingServiceExtensions.WebhookPolicy)]
public class SmsReceiptsController : ControllerBase
{
    private readonly IConsentInvitationService _consent;
    private readonly IDateTimeProvider _dateTime;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmsReceiptsController> _logger;

    public SmsReceiptsController(
        IConsentInvitationService consent, IDateTimeProvider dateTime, IConfiguration configuration, ILogger<SmsReceiptsController> logger)
    {
        _consent = consent;
        _dateTime = dateTime;
        _configuration = configuration;
        _logger = logger;
    }

    /// <param name="Status">"delivered", or "failed" / "undelivered" / "rejected".</param>
    public record SmsReceiptRequest(string ProviderMessageId, string Status, DateTime? Timestamp, string? Reason);

    [HttpPost("receipts")]
    public async Task<IActionResult> Receive(
        [FromBody] SmsReceiptRequest request,
        [FromHeader(Name = "X-Webhook-Secret")] string? secret,
        CancellationToken cancellationToken)
    {
        var expected = _configuration["Sms:ReceiptSecret"];
        if (string.IsNullOrWhiteSpace(expected))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "SMS delivery receipts are not configured." });

        if (string.IsNullOrEmpty(secret) || !FixedTimeEquals(secret, expected))
            return Unauthorized();

        var status = request.Status?.Trim().ToLowerInvariant() switch
        {
            "delivered" => ConsentInvitationStatus.Delivered,
            "failed" or "undelivered" or "rejected" => ConsentInvitationStatus.Failed,
            _ => (ConsentInvitationStatus?)null
        };
        if (status is null)
            return BadRequest(new { message = "Status must be 'delivered' or 'failed'." });

        var at = request.Timestamp is { } ts ? DateTime.SpecifyKind(ts, DateTimeKind.Utc) : _dateTime.UtcNow;
        var applied = await _consent.ApplyReceiptAsync(request.ProviderMessageId, status.Value, at, request.Reason, cancellationToken);
        _logger.LogInformation("SMS receipt for {ProviderMessageId}: {Status}, applied {Applied}", request.ProviderMessageId, status, applied);

        // 202 either way: a duplicate or an unknown id is not an error the provider can fix, and retrying it would only repeat.
        return Accepted(new { applied });
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
