using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One billing notification to a tenant: the in-app alert the tenant sees (this row IS the in-app channel)
/// plus how the email and WhatsApp copies went. <see cref="EpisodeKey"/> is what stops a threshold alerting
/// again and again - for a quota alert it is the id of the ledger entry that last added units, so a top-up
/// or renewal starts a fresh episode and the next dip below the threshold alerts once more.
/// </summary>
public class TenantNotification : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public TenantNotificationKind Kind { get; set; }

    public QuotaType? QuotaType { get; set; }

    public string EpisodeKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DeliveryStatus EmailStatus { get; set; } = DeliveryStatus.NotAttempted;

    public DeliveryStatus WhatsAppStatus { get; set; } = DeliveryStatus.NotAttempted;

    /// <summary>Why a channel failed or was skipped, for support - never shown to the tenant.</summary>
    public string? DeliveryNote { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }
}
