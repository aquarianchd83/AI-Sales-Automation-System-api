namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>When the campaign an Auto-Campaign execution generates should start - see
/// LeadDiscoveryProfile.AutoCampaignStartMode/AutoCampaignStartTime and
/// LeadDiscoveryRunService.EnsureCampaignAsync.</summary>
public enum LeadDiscoveryCampaignStartMode
{
    /// <summary>Starts as soon as it is created (Campaign.ScheduledStartAt left null).</summary>
    Immediate = 0,

    /// <summary>Starts the day after the execution's processing date, at AutoCampaignStartTime (the
    /// tenant's own local time, same meaning as Campaign.ScheduledStartAt).</summary>
    NextDayWithTime = 1
}
