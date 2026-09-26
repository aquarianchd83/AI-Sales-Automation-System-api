namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What happened when a discovered customer was run through auto-campaign enrollment - see
/// Entities.Campaigns.AutoCampaignEnrollment (legacy, no longer written).</summary>
public enum AutoCampaignEnrollmentStatus
{
    /// <summary>Cloned/reused an execution campaign and attached the customer to it.</summary>
    Started = 0,

    /// <summary>Nothing was created - auto campaign is off, no source campaign is configured, or this
    /// customer was already enrolled for this source campaign before. Not a failure.</summary>
    Skipped = 1,

    /// <summary>An execution campaign was attempted but something went wrong (source campaign not
    /// usable, clone/attach/schedule error) - see AutoCampaignEnrollment.Reason.</summary>
    Failed = 2
}
