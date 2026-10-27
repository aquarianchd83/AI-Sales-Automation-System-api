using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// One entry in the review trail of a script: who did what, when, from which state to which, with their words and the fingerprint of what they
/// were looking at. Entries are only ever added - never changed or removed - so the record of how a script came to be approved is whole.
/// </summary>
public class ContentReviewEvent : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid VersionId { get; set; }

    public ContentReviewAction Action { get; set; }

    public ContentVersionStatus? FromStatus { get; set; }

    public ContentVersionStatus ToStatus { get; set; }

    public Guid? ActorUserId { get; set; }

    public string? ActorEmail { get; set; }

    /// <summary>The reason for a rejection, or the note that came with the step.</summary>
    public string? Comment { get; set; }

    /// <summary>The script's fingerprint at that moment (empty when not computed).</summary>
    public string SnapshotHash { get; set; } = string.Empty;

    public DateTime At { get; set; }
}
