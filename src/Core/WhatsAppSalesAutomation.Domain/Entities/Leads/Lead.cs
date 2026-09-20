using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Leads;

/// <summary>A customer's current sales-qualification state. One active Lead per customer at a time
/// (mirroring Conversation's one-active-thread rule) - re-engagement after Lost/Won creates a fresh
/// Lead rather than reopening the old one, so the pipeline board's history stays an honest record of
/// each attempt.</summary>
public class Lead : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>The campaign that originated this lead, if any - null for leads that started from an
    /// inbound conversation with no campaign attribution.</summary>
    public Guid? CampaignId { get; set; }

    public LeadStage Stage { get; set; } = LeadStage.New;

    public LeadScoreBand Score { get; set; } = LeadScoreBand.Cold;

    /// <summary>The underlying number Score is banded from - keeps the pipeline board's Hot/Warm/Cold
    /// filter and a future numeric sort/report both meaningful without picking one representation.</summary>
    public int ScoreNumeric { get; set; }

    /// <summary>Denormalized mirror of the "budget" QualificationField's current value, kept in sync
    /// by the capture path. Retained alongside <see cref="QualificationValues"/> so the existing lead
    /// list, filters, reports and <c>UpdateLeadRequest</c> keep working unchanged while the UI moves
    /// to dynamic fields; null for a tenant whose schema has no such field.</summary>
    public string? Budget { get; set; }

    /// <summary>Denormalized mirror of the "interest" QualificationField - see <see cref="Budget"/>.</summary>
    public string? Interest { get; set; }

    /// <summary>Denormalized mirror of the "purchase_timeline" QualificationField - see <see cref="Budget"/>.</summary>
    public string? PurchaseTimeline { get; set; }

    public Guid? AssignedTo { get; set; }

    public DateTime? LastActivityAt { get; set; }

    /// <summary>Concurrency token. AI-driven score updates (after every AiInteraction) and a manual
    /// agent edit can race on the same row; EF's optimistic concurrency check turns that race into a
    /// retry instead of a silently lost update - same reasoning as CampaignCustomer.RowVersion.</summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    /// <summary>The AI's current read of what the customer wants, as a <see cref="CustomerIntent"/>
    /// name. Lead-level rather than only on Conversation because a lead can span more than one
    /// conversation and the pipeline board filters on the lead.</summary>
    public string? CurrentIntent { get; set; }

    /// <summary>When the hot-lead condition first fired. Non-null means qualification is paused: the
    /// agent stops asking and moves to the next step instead. Cleared only by a human - once a lead is
    /// ready to act, an agent going back to asking qualification questions is exactly the behaviour
    /// this is here to prevent.</summary>
    public DateTime? HotLeadDetectedAt { get; set; }

    /// <summary>Which signal made it hot, for the handoff briefing - a scoring rule's DisplayName, an
    /// intent name, or "score threshold".</summary>
    public string? HotLeadReason { get; set; }

    public ICollection<LeadActivity> Activities { get; set; } = new List<LeadActivity>();

    public ICollection<LeadQualificationValue> QualificationValues { get; set; } = new List<LeadQualificationValue>();

    public ICollection<LeadScoreContribution> ScoreContributions { get; set; } = new List<LeadScoreContribution>();
}
