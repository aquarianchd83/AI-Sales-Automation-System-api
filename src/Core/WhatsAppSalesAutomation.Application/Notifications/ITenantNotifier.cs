using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Notifications;

public record TenantNotificationRequest(
    Guid TenantId,
    TenantNotificationKind Kind,
    QuotaType? QuotaType,
    string EpisodeKey,
    string Title,
    string Body,
    bool AlsoWhatsApp,
    // WhatsApp goes to the support phone the tenant keeps on its profile (when it has a usable one) instead of its billing alert number.
    bool ToSupportPhone = false,
    // When the notice's own template cannot be used (missing, switched off, not yet approved by Meta) send the same words as plain text instead of skipping.
    // WhatsApp delivers plain text from a business only while the person has messaged that number in the last 24 hours, so this can still fail: the
    // notification then records Failed with Meta's reason rather than pretending it went.
    bool PlainTextFallback = false);

/// <summary>Tells a tenant something about its billing: always in-app, by email when there's an address, and on
/// WhatsApp when asked and the tenant has opted in with a number. Never throws - a delivery problem is recorded
/// on the notification, not raised into whatever triggered it.</summary>
public interface ITenantNotifier
{
    /// <summary>False, having done nothing, when this exact (kind, quota type, episode) was already sent.</summary>
    Task<bool> NotifyAsync(TenantNotificationRequest request, CancellationToken cancellationToken = default);
}
