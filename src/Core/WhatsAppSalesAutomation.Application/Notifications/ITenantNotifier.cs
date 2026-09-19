using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Notifications;

public record TenantNotificationRequest(
    Guid TenantId,
    TenantNotificationKind Kind,
    QuotaType? QuotaType,
    string EpisodeKey,
    string Title,
    string Body,
    bool AlsoWhatsApp);

/// <summary>Tells a tenant something about its billing: always in-app, by email when there's an address, and on
/// WhatsApp when asked and the tenant has opted in with a number. Never throws - a delivery problem is recorded
/// on the notification, not raised into whatever triggered it.</summary>
public interface ITenantNotifier
{
    /// <summary>False, having done nothing, when this exact (kind, quota type, episode) was already sent.</summary>
    Task<bool> NotifyAsync(TenantNotificationRequest request, CancellationToken cancellationToken = default);
}
