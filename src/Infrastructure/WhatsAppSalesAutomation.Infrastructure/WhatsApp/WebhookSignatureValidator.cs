using System.Security.Cryptography;
using System.Text;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Verifies Meta's X-Hub-Signature-256 header against the raw request body before anything about a
/// webhook POST is trusted (Phase 1 §9). Pure crypto, with no business-logic abstraction to gain from
/// an Application-layer interface - kept entirely in Infrastructure and injected straight into the
/// webhook controller, unlike the provider abstractions (IWhatsAppService etc.) that genuinely need to
/// hide behind Application. Takes the secret to check against as a parameter rather than reading one
/// fixed global setting - since each tenant supplies their own AppSecret (Phase 2), the caller
/// (WebhooksController.Receive) resolves the right tenant first, then passes that tenant's own secret
/// in here, rather than this validator knowing which tenant it's checking.
/// </summary>
public interface IWebhookSignatureValidator
{
    bool IsValid(byte[] rawBody, string? signatureHeader, string appSecret);
}

public class WebhookSignatureValidator : IWebhookSignatureValidator
{
    private const string SignaturePrefix = "sha256=";

    public bool IsValid(byte[] rawBody, string? signatureHeader, string appSecret)
    {
        // An unconfigured/empty secret should fail closed, not pass everything - callers only ever
        // reach this with a tenant they already resolved a config row for, so an empty AppSecret here
        // means that row is incomplete, not that signature checking should be skipped.
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(appSecret))
            return false;

        if (!signatureHeader.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var providedHex = signatureHeader[SignaturePrefix.Length..];

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var computedHex = Convert.ToHexString(hmac.ComputeHash(rawBody));

        // Fixed-time comparison: a signature check that returns faster on an early mismatched
        // character leaks how many leading hex digits an attacker's guess got right.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(providedHex.ToLowerInvariant()));
    }
}
