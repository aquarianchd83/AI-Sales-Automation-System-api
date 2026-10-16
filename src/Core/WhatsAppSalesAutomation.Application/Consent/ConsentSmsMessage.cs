using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Application.Common;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// The SMS that invites a customer to start a WhatsApp conversation, and the checks it must pass before it is sent. Nothing about a
/// tenant is hard-coded: the business name and the wa.me destination come from the tenant's own record.
/// </summary>
public static class ConsentSmsMessage
{
    /// <summary>A longer name is shortened so the invitation stays within a few SMS segments.</summary>
    public const int MaxBusinessNameLength = 60;

    /// <summary>Three SMS segments of GSM-7 text; the invitation is a sentence and a link, far below this.</summary>
    public const int MaxBodyLength = 459;

    /// <summary>How many times one invitation is tried (the first send included) before it stays Failed.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Minutes to wait before the second and third attempt.</summary>
    public static readonly int[] RetryBackoffMinutes = { 5, 30, 120 };

    private static readonly Regex E164 = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);

    /// <summary>The wa.me link for the tenant's WhatsApp number: the country code and number, digits only.</summary>
    public static bool TryBuildLink(string? tenantWhatsAppNumber, out string link, out string failure)
    {
        link = string.Empty;
        failure = string.Empty;

        if (string.IsNullOrWhiteSpace(tenantWhatsAppNumber))
        {
            failure = "The business's WhatsApp number is not set up, so the invitation has nowhere to point.";
            return false;
        }

        if (!PhoneNumberNormalizer.TryNormalize(tenantWhatsAppNumber, out var e164))
        {
            failure = $"The business's WhatsApp number '{tenantWhatsAppNumber}' is not a valid phone number.";
            return false;
        }

        link = $"https://wa.me/{e164[1..]}";
        return true;
    }

    /// <summary>"Hi, [Business Name] here. Chat with us on WhatsApp: https://wa.me/91XXXXXXXXXX".</summary>
    public static string BuildBody(string businessName, string link)
    {
        var name = businessName.Trim();
        if (name.Length > MaxBusinessNameLength)
            name = name[..(MaxBusinessNameLength - 1)].TrimEnd() + "…";

        return $"Hi, {name} here. Chat with us on WhatsApp: {link}";
    }

    /// <summary>The content checks: a valid destination number, a non-empty body within length, and a secure wa.me link in it.
    /// Returns why it must not be sent, or null when it may.</summary>
    public static string? Preflight(string toPhoneE164, string body, string link)
    {
        if (!E164.IsMatch(toPhoneE164))
            return $"The customer's number '{toPhoneE164}' is not a valid mobile number.";

        if (string.IsNullOrWhiteSpace(body))
            return "The invitation text is empty.";

        if (body.Length > MaxBodyLength)
            return $"The invitation is {body.Length} characters, over the {MaxBodyLength} an SMS may carry.";

        if (!link.StartsWith("https://wa.me/", StringComparison.Ordinal) || !body.Contains(link, StringComparison.Ordinal))
            return "The invitation does not carry a valid WhatsApp link.";

        return null;
    }

    /// <summary>When a failed attempt may be retried, or null when the attempts are used up.</summary>
    public static DateTime? NextAttemptAfter(int attemptsMade, DateTime now) =>
        attemptsMade >= MaxAttempts ? null : now.AddMinutes(RetryBackoffMinutes[Math.Min(attemptsMade, RetryBackoffMinutes.Length - 1)]);
}
