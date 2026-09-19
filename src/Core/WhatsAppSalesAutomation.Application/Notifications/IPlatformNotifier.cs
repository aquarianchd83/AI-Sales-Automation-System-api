using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Notifications;

public record PlatformNotificationRequest(
    PlatformNotificationKind Kind,
    PlatformNotificationSeverity Severity,
    string EpisodeKey,
    string Title,
    string Body,
    Guid? TenantId = null,
    string? JobType = null);

/// <summary>Raises an alert for the platform operators - an in-app row every PlatformSuperAdmin sees, plus
/// an email to each. Never throws: an alert failing to send must not fail the work that raised it.</summary>
public interface IPlatformNotifier
{
    /// <summary>False when the same (kind, tenant, job, episode) alert already exists, or it could not be raised.</summary>
    Task<bool> NotifyAsync(PlatformNotificationRequest request, CancellationToken cancellationToken = default);
}
