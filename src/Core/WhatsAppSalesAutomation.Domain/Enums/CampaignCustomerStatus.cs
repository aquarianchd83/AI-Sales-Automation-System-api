namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// A customer's progress through a single campaign. Which step they are on lives in
/// <c>CampaignCustomer.CurrentStepNumber</c> rather than in this enum - collapsing "InitialSent",
/// "FollowUp1Sent" etc. into one <see cref="AwaitingResponse"/> state keeps the state machine the
/// sender/follow-up jobs drive small, while the step number still answers "which message went out".
/// </summary>
public enum CampaignCustomerStatus
{
    /// <summary>Attached to the campaign; the initial message has not gone out yet.</summary>
    Pending = 0,

    /// <summary>A message has gone out and the next follow-up (if any) is scheduled.</summary>
    AwaitingResponse = 1,

    /// <summary>The customer replied. Automation for this campaign stops; handled by Phase 4/5.</summary>
    Responded = 2,

    /// <summary>Opted out during this campaign's run. Terminal.</summary>
    OptedOut = 3,

    /// <summary>Escalated to a human. Terminal for this campaign's automation.</summary>
    HandedOff = 4,

    /// <summary>All configured steps were sent with no response. Terminal.</summary>
    Completed = 5,

    /// <summary>Every send attempt for the current step exhausted its retries. Terminal.</summary>
    Failed = 6
}
