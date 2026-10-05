namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Where a scheduled "follow up later" reminder for a lead stands. <see cref="Scheduled"/> is the only
/// state the sender job looks at; everything else is history.</summary>
public enum LeadFollowUpStatus
{
    /// <summary>Waiting for its due date.</summary>
    Scheduled = 0,

    /// <summary>The follow-up message went out.</summary>
    Sent = 1,

    /// <summary>A person called it off, a newer follow-up replaced it, or the lead/customer is no longer
    /// one to follow up (won, lost, opted out).</summary>
    Cancelled = 2,

    /// <summary>Not sent because the customer got in touch on their own after it was scheduled - they no longer
    /// need nudging, so it would only irritate.</summary>
    Skipped = 3,

    /// <summary>WhatsApp refused the send. A person can retry it with Send now.</summary>
    Failed = 4,

    /// <summary>The AI noticed the customer is interested but cannot proceed and proposed a follow-up. Nothing is
    /// ever sent from this state: a person confirms it (which schedules it with a template) or dismisses it.</summary>
    Suggested = 5
}
