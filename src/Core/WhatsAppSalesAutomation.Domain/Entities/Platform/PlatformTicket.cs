using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// A work item for the platform's product owner, raised by the system (or later by hand) when something only the platform can fix has
/// affected a tenant. Platform-owned and shared, deliberately NOT <see cref="ITenantOwned"/> - the same stance as <see cref="PlatformNotification"/>.
/// <see cref="TenantId"/> only points at the tenant it is about.
/// <see cref="DedupeKey"/> is what stops a tenant retrying the same broken thing from filing a ticket every time: while one with the key is not
/// Resolved, no second is raised.
/// </summary>
public class PlatformTicket : BaseEntity
{
    public Guid? TenantId { get; set; }

    public PlatformTicketKind Kind { get; set; }

    public string DedupeKey { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public PlatformTicketPriority Priority { get; set; } = PlatformTicketPriority.Normal;

    public PlatformTicketStatus Status { get; set; } = PlatformTicketStatus.Open;

    public Guid? AssignedToUserId { get; set; }

    /// <summary>How the product owner was told: the bell, email and WhatsApp results, one short line each.</summary>
    public string? DeliveryNote { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }
}
