using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Leads;

/// <summary>A customer's current sales-qualification state. One active Lead per customer at a time
/// (mirroring Conversation's one-active-thread rule) - re-engagement after Lost/Won creates a fresh
/// Lead rather than reopening the old one, so the pipeline board's history stays an honest record of
/// each attempt.</summary>
public class Lead : BaseEntity
{
    public Guid CustomerId { get; set; }

    /// <summary>The campaign that originated this lead, if any - null for leads that started from an
    /// inbound conversation with no campaign attribution.</summary>
    public Guid? CampaignId { get; set; }

    public LeadStage Stage { get; set; } = LeadStage.New;

    public LeadScoreBand Score { get; set; } = LeadScoreBand.Cold;

    /// <summary>The underlying number Score is banded from - keeps the pipeline board's Hot/Warm/Cold
    /// filter and a future numeric sort/report both meaningful without picking one representation.</summary>
    public int ScoreNumeric { get; set; }

    public string? Budget { get; set; }

    public string? Interest { get; set; }

    public string? PurchaseTimeline { get; set; }

    public Guid? AssignedTo { get; set; }

    public DateTime? LastActivityAt { get; set; }

    /// <summary>Concurrency token. AI-driven score updates (after every AiInteraction) and a manual
    /// agent edit can race on the same row; EF's optimistic concurrency check turns that race into a
    /// retry instead of a silently lost update - same reasoning as CampaignCustomer.RowVersion.</summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ICollection<LeadActivity> Activities { get; set; } = new List<LeadActivity>();
}
