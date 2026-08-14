using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Campaigns;

public class Campaign : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

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
