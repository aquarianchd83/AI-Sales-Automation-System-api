namespace WhatsAppSalesAutomation.Application.Notifications;

/// <summary><paramref name="Skipped"/> means the channel isn't set up on this platform (no SMTP host, no platform
/// WhatsApp credentials) - not that a delivery failed.</summary>
public record DeliveryResult(bool Success, string? Note = null, bool Skipped = false);

/// <summary>Sends one email. The implementation decides how (SMTP when configured, otherwise a log line) -
/// callers only learn whether it went.</summary>
public interface IEmailSender
{
    Task<DeliveryResult> SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends a template message from the PLATFORM's own WhatsApp number, never a tenant's: a billing alert has to
/// work when the tenant's own WhatsApp quota is zero, and it must not spend that quota. Business-initiated, so
/// it needs an approved template and costs the platform a Utility-category message.
/// </summary>
public interface IPlatformWhatsAppSender
{
    Task<DeliveryResult> SendTemplateAsync(string toPhoneE164, string templateName, string languageCode, IReadOnlyList<string> parameters, CancellationToken cancellationToken = default);
}
