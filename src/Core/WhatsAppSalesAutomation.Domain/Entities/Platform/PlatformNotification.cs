using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// An alert for the platform operators (every PlatformSuperAdmin) - the counterpart of
/// <c>TenantNotification</c>, which only ever reaches a tenant. This row IS the in-app alert; the email
/// copy is best-effort and recorded alongside it.
///
/// Platform-owned and shared, deliberately NOT <see cref="ITenantOwned"/>: any operator can read and
/// acknowledge it, and it is raised from background runs that have no ambient tenant.
/// <see cref="TenantId"/> is only a pointer to the tenant the alert is about.
/// <see cref="EpisodeKey"/> is what keeps a retry or a re-run from raising the same alert twice.
/// </summary>
public class PlatformNotification : BaseEntity
{
    public PlatformNotificationKind Kind { get; set; }

    public PlatformNotificationSeverity Severity { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>A <c>TenantJobTypes</c> constant when the alert is about a job, else null.</summary>
    public string? JobType { get; set; }

    public string EpisodeKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DeliveryStatus EmailStatus { get; set; } = DeliveryStatus.NotAttempted;

    public string? DeliveryNote { get; set; }

    /// <summary>Shared, not per-operator: one operator acknowledging it clears it for all of them.</summary>
    public DateTime? AcknowledgedAtUtc { get; set; }
}
