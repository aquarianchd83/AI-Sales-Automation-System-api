using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Campaigns;

public class Campaign : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

    /// <summary>
    /// PINNED TO INDIA STANDARD TIME (UTC+5:30) - unlike every other timestamp on this entity, which
    /// is a true UTC system timestamp. This is deliberate: the entire customer base is India-only, and
    /// leaving a user-facing "which day should this start" field to whatever offset a client happens
    /// to send caused a real bug - a client that computes UTC-of-local-midnight before sending (the
    /// default behaviour of most JS date pickers) would land on the previous UTC calendar day, and a
    /// server-side comparison against <c>DateTime.UtcNow</c> would then be off by up to 5:30.
    /// Compare this value only against <c>IDateTimeProvider.IstNow</c>, never <c>UtcNow</c> - see
    /// <c>CampaignService.StartAsync</c> and <c>CampaignSendService.ProcessInitialSendsAsync</c>.
    /// </summary>
    public DateTime? ScheduledStartAt { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? StoppedAt { get; set; }

    /// <summary>
    /// Snapshot of how the audience was selected (e.g. tag names), kept for the record. The audience
    /// itself is materialized into <see cref="CampaignCustomers"/> at attach time and is not
    /// re-evaluated dynamically - a customer added to a tag after attachment is not swept in.
    /// </summary>
    public string? TargetAudienceFilterJson { get; set; }

    public ICollection<CampaignStep> Steps { get; set; } = new List<CampaignStep>();

    public ICollection<CampaignCustomer> CampaignCustomers { get; set; } = new List<CampaignCustomer>();
}
