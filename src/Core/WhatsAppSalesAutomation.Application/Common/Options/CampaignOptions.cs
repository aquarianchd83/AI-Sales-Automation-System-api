namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>Bound from the "Campaigns" config section.</summary>
public class CampaignOptions
{
    /// <summary>Phase 1 design requires 2-5 media items per step; kept configurable so a strict
    /// default does not block local testing before real media assets exist.</summary>
    public int MinStepMedia { get; set; } = 2;

    public int MaxStepMedia { get; set; } = 5;
}
