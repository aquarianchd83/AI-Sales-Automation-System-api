using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.Razorpay;

/// <summary>
/// The two signatures Razorpay uses, both HMAC-SHA256 as lowercase hex:
/// <list type="bullet">
/// <item>Checkout: <c>HMAC(key_secret, order_id + "|" + payment_id)</c>, returned to the browser as razorpay_signature. The browser cannot be
/// trusted to say a payment succeeded - this signature, checked on the server, is the proof.</item>
/// <item>Webhook: <c>HMAC(webhook_secret, raw request body)</c> in the X-Razorpay-Signature header. Computed over the exact bytes received,
/// never a re-serialised copy.</item>
/// </list>
/// Comparisons are constant-time.
/// </summary>
public static class RazorpaySignatures
{
    public static string Compute(string secret, byte[] message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(message)).ToLowerInvariant();
    }

    public static string PaymentSignature(string orderId, string paymentId, string keySecret) =>
        Compute(keySecret, Encoding.UTF8.GetBytes($"{orderId}|{paymentId}"));

    public static bool IsValidPaymentSignature(string orderId, string paymentId, string? signature, string keySecret) =>
        Matches(PaymentSignature(orderId, paymentId, keySecret), signature);

    public static bool IsValidWebhookSignature(byte[] body, string? signature, string webhookSecret) =>
        !string.IsNullOrEmpty(webhookSecret) && Matches(Compute(webhookSecret, body), signature);

    private static bool Matches(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;

        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(actual.Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>Key ids look like rzp_test_XXXXXXXX or rzp_live_XXXXXXXX; the prefix is the only reliable sign of which mode they are.</summary>
public static class RazorpayKeys
{
    public static readonly Regex KeyIdPattern = new(@"^rzp_(test|live)_[A-Za-z0-9]{6,40}$", RegexOptions.Compiled);

    public static string ModeOf(string keyId) => keyId.StartsWith("rzp_live_", StringComparison.Ordinal) ? "live" : "test";
}
