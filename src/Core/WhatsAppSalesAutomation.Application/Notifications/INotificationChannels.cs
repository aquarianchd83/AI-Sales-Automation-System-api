using WhatsAppSalesAutomation.Application.Common.Interfaces;

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

/// <summary>Sends a one-time code by SMS. Provider-specific (the provider's registered template carries the wording), so
/// the interface is the code and the number, nothing more. Skipped - not failed - when SMS is not set up.</summary>
public interface ISmsOtpSender
{
    /// <param name="toPhoneE164">Digits with the country code, with or without a leading +.</param>
    Task<DeliveryResult> SendOtpAsync(string toPhoneE164, string code, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends a template message from the PLATFORM's own WhatsApp number, never a tenant's: a billing alert has to
/// work when the tenant's own WhatsApp quota is zero, and it must not spend that quota. Business-initiated, so
/// it needs an approved template and costs the platform a Utility-category message.
/// </summary>
public interface IPlatformWhatsAppSender
{
    /// <param name="mediaUrl">The public link of the image the template shows as its header, when it has one.</param>
    Task<DeliveryResult> SendTemplateAsync(
        string toPhoneE164, string templateName, string languageCode, IReadOnlyList<string> parameters,
        CancellationToken cancellationToken = default, string? mediaUrl = null);
}

/// <summary>
/// The platform's own WhatsApp Business Account, seen from the template side: what Meta has registered and reviewed, and creating or editing
/// the platform's notice templates there. The credentials are the platform admin's (PlatformWhatsApp:* settings), never a tenant's.
/// </summary>
public interface IPlatformWhatsAppTemplateAdmin
{
    /// <summary>True when the platform number has credentials and has not been switched off.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default);

    Task<WhatsAppTemplateSubmitResult> CreateTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default);

    Task<WhatsAppTemplateSubmitResult> UpdateTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default);
}
