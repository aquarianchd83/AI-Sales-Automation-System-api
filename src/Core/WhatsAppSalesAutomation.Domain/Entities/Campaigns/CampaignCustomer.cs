using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Campaigns;

/// <summary>Join between a campaign and a customer that also tracks that customer's progress through it.</summary>
public class CampaignCustomer : BaseEntity
{
    public Guid CampaignId { get; set; }

    public Guid CustomerId { get; set; }

    public CampaignCustomerStatus Status { get; set; } = CampaignCustomerStatus.Pending;

    /// <summary>0 = Initial. Set once that step's message has actually gone out, not while queued.</summary>
    public int CurrentStepNumber { get; set; } = -1;

    public DateTime? LastMessageSentAt { get; set; }

    public DateTime? LastCustomerResponseAt { get; set; }

    /// <summary>When the next follow-up becomes eligible to send. Null once there is no next step.</summary>
    public DateTime? NextFollowUpDueAt { get; set; }

    public string? StoppedReason { get; set; }

    /// <summary>
    /// Concurrency token. The follow-up scheduler and a manual pause/opt-out can race on the same
    /// row; EF's optimistic concurrency check turns that race into a retry instead of a lost update.
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
