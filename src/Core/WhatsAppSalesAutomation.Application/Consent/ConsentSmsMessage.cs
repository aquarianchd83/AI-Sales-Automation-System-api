using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// The one SMS a customer gets: a short note and a link to the tenant's consent page. It does not say, or imply, that the customer has
/// agreed to anything - agreeing happens on the page. Nothing about a tenant is hard-coded: the names come from its published page.
/// </summary>
public static class ConsentSmsMessage
{
    /// <summary>A longer name is shortened so the message stays within a few SMS segments.</summary>
    public const int MaxBusinessNameLength = 60;

    public const int MaxProductLength = 60;

    public const int MaxFirstNameLength = 30;

    /// <summary>Three SMS segments of GSM-7 text.</summary>
    public const int MaxBodyLength = 459;

    /// <summary>How many times one invitation is tried (the first send included) before it stays Failed.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Minutes to wait before the second and third attempt.</summary>
    public static readonly int[] RetryBackoffMinutes = { 5, 30, 120 };

    private static readonly Regex E164 = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);

    /// <summary>"{base}/consent/{token}" - the token is the only thing in it that is specific to the customer.</summary>
    public static string BuildLink(string publicBaseUrl, string token) => $"{publicBaseUrl.TrimEnd('/')}/consent/{token}";

    /// <summary>"Hi Asha! Would you like to learn how Acme can help with eye care? View the details and choose whether to receive WhatsApp follow-ups: https://…"</summary>
    public static string BuildBody(string? customerFirstName, string businessName, string? productLabel, string link)
    {
        var first = Shorten(customerFirstName, MaxFirstNameLength);
        var greeting = string.IsNullOrEmpty(first) ? "Hi!" : $"Hi {first}!";
        var business = Shorten(businessName, MaxBusinessNameLength);
        var product = Shorten(productLabel, MaxProductLength);

        var question = string.IsNullOrEmpty(product)
            ? $"Would you like to learn about {business}?"
            : $"Would you like to learn how {business} can help with {product}?";

        return $"{greeting} {question} View the details and choose whether to receive WhatsApp follow-ups: {link}";
    }

    /// <summary>The same note with the secret taken out, for the record: the raw token is never stored.</summary>
    public static string Redact(string body, string token) => body.Replace(token, "[token]", StringComparison.Ordinal);

    /// <summary>Why this number cannot be texted, or null when it can.</summary>
    public static string? CheckNumber(string toPhoneE164) =>
        E164.IsMatch(toPhoneE164) ? null : $"The customer's number '{toPhoneE164}' is not a valid mobile number.";

    /// <summary>The content checks: a valid destination number, a non-empty body within length, and a secure link in it.
    /// Returns why it must not be sent, or null when it may.</summary>
    public static string? Preflight(string toPhoneE164, string body, string link)
    {
        if (!E164.IsMatch(toPhoneE164))
            return $"The customer's number '{toPhoneE164}' is not a valid mobile number.";

        if (string.IsNullOrWhiteSpace(body))
            return "The message text is empty.";

        if (body.Length > MaxBodyLength)
            return $"The message is {body.Length} characters, over the {MaxBodyLength} an SMS may carry.";

        if (!(link.StartsWith("https://", StringComparison.Ordinal) || link.StartsWith("http://localhost", StringComparison.Ordinal))
            || !body.Contains(link, StringComparison.Ordinal))
            return "The message does not carry a valid consent link.";

        return null;
    }

    /// <summary>When a failed attempt may be retried, or null when the attempts are used up.</summary>
    public static DateTime? NextAttemptAfter(int attemptsMade, DateTime now) =>
        attemptsMade >= MaxAttempts ? null : now.AddMinutes(RetryBackoffMinutes[Math.Min(attemptsMade, RetryBackoffMinutes.Length - 1)]);

    private static string Shorten(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }
}
