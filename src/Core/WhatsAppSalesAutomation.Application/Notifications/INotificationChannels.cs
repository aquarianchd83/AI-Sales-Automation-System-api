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

    /// <summary>Plain text, with no template. Meta only delivers it inside the 24-hour window after the person last messaged the platform number, so it is
    /// the fallback for a notice whose template is not available, not the normal way.</summary>
    Task<DeliveryResult> SendTextAsync(string toPhoneE164, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// The platform's own WhatsApp Business Account, seen from the template side: what Meta has registered and reviewed, and creating or editing
/// the platform's notice templates there. The credentials are the platform admin's (PlatformWhatsApp:* settings), never a tenant's.
/// </summary>
public interface IPlatformWhatsAppTemplateAdmin
{
    /// <summary>True when the platform number has credentials and has not been switched off.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    /// <summary>Every template on the platform's WhatsApp Business Account. A failed read comes back with <see cref="PlatformRemoteTemplates.Error"/>
    /// set rather than as an empty list, so "Meta has none of these" and "Meta could not be asked" are never confused.</summary>
    Task<PlatformRemoteTemplates> GetTemplatesAsync(CancellationToken cancellationToken = default);

    Task<WhatsAppTemplateSubmitResult> CreateTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default);

    Task<WhatsAppTemplateSubmitResult> UpdateTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default);
}

/// <summary>What Meta holds for the platform's WhatsApp Business Account, or why it could not be read.</summary>
public record PlatformRemoteTemplates(IReadOnlyList<WhatsAppRemoteTemplate> Templates, string? Error)
{
    public static PlatformRemoteTemplates Failed(string error) => new(Array.Empty<WhatsAppRemoteTemplate>(), error);
}
