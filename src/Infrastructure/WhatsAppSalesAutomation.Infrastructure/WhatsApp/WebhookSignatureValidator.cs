using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Verifies Meta's X-Hub-Signature-256 header against the raw request body before anything about a
/// webhook POST is trusted (Phase 1 §9). Pure crypto against config, with no business-logic
/// abstraction to gain from an Application-layer interface - kept entirely in Infrastructure and
/// injected straight into the webhook controller, unlike the provider abstractions (IWhatsAppService
/// etc.) that genuinely need to hide behind Application.
/// </summary>
public interface IWebhookSignatureValidator
{
    bool IsValid(byte[] rawBody, string? signatureHeader);
}

public class WebhookSignatureValidator : IWebhookSignatureValidator
{
    private const string SignaturePrefix = "sha256=";

    private readonly WhatsAppSettings _settings;

    public WebhookSignatureValidator(IOptions<WhatsAppSettings> settings)
    {
        _settings = settings.Value;
    }

    public bool IsValid(byte[] rawBody, string? signatureHeader)
    {
        // No AppSecret configured is only expected in local/dev with the Simulated provider - a real
        // deployment must set one, and an unconfigured secret should fail closed, not pass everything.
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(_settings.AppSecret))
            return false;

        if (!signatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var providedHex = signatureHeader[SignaturePrefix.Length..];

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_settings.AppSecret));
        var computedHex = Convert.ToHexString(hmac.ComputeHash(rawBody));

        // Fixed-time comparison: a signature check that returns faster on an early mismatched
        // character leaks how many leading hex digits an attacker's guess got right.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(providedHex.ToLowerInvariant()));
    }
}
