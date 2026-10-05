using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Leads;

/// <summary>
/// A lead who was interested but could not go ahead right now (budget, timing, a decision still pending),
/// parked to be contacted again after a chosen wait - typically 1, 2 or 3 months. Campaign follow-ups cannot
/// cover this: they only chase customers who have not replied, and stop for good the moment someone does.
///
/// A lead has at most one <see cref="LeadFollowUpStatus.Scheduled"/> row at a time; scheduling another
/// cancels the earlier one. The rows that are no longer Scheduled stay as the history of how often this
/// customer has been nudged - which is what the per-lead cap on follow-ups counts.
/// </summary>
public class LeadFollowUp : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid LeadId { get; set; }

    public Guid CustomerId { get; set; }

    public LeadFollowUpStatus Status { get; set; } = LeadFollowUpStatus.Scheduled;

    /// <summary>When the message becomes eligible to send, UTC. The job sends on or after this, never before,
    /// and only inside the tenant's daytime sending hours.</summary>
    public DateTime DueAt { get; set; }

    /// <summary>The 1/2/3-month choice this was scheduled from, or null when an exact date was picked. Kept for
    /// display only; <see cref="DueAt"/> is what the sender uses.</summary>
    public int? IntervalMonths { get; set; }

    /// <summary>Why the customer could not proceed, in the agent's words - shown in the list so whoever
    /// follows up knows the context.</summary>
    public string? Reason { get; set; }

    /// <summary>The approved template the follow-up goes out as. A business-initiated message outside WhatsApp's
    /// 24-hour window must be a template.</summary>
    public Guid MessageTemplateId { get; set; }

    /// <summary>Which follow-up this is for the lead: 1 for the first, 2 for the next. Set when it is scheduled.</summary>
    public int FollowUpNumber { get; set; }

    /// <summary>How many sends have been attempted. Part of the message idempotency key so a retry after a
    /// failure is a new message rather than a collision with the failed one.</summary>
    public int AttemptCount { get; set; }

    public DateTime? SentAt { get; set; }

    public Guid? MessageId { get; set; }

    /// <summary>Why it ended the way it did - the cancel/skip reason or WhatsApp's error. Null while Scheduled
    /// and after a clean send.</summary>
    public string? OutcomeNote { get; set; }

    /// <summary>The agent who scheduled it.</summary>
    public Guid? ScheduledBy { get; set; }
}
