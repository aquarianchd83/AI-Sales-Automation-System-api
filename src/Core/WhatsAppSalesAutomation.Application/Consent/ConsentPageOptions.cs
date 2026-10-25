namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>Bound from the "ConsentPage" configuration section.</summary>
public class ConsentPageOptions
{
    /// <summary>
    /// Where the customer-facing app is served, e.g. https://app.example.com. The consent link is "{PublicBaseUrl}/consent/{token}".
    /// Without it no link can be built, so discovery is held back rather than charged for an SMS that cannot be sent.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>How long a consent link works after it is created.</summary>
    public int TokenLifetimeDays { get; set; } = 14;

    /// <summary>How many times the WhatsApp start is retried after a provider failure.</summary>
    public int MaxWhatsAppAttempts { get; set; } = 3;
}
