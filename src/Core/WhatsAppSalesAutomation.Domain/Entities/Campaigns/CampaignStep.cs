using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Campaigns;

/// <summary>One message in a campaign sequence: the Initial send, or one of up to four follow-ups.</summary>
public class CampaignStep : BaseEntity
{
    public Guid CampaignId { get; set; }

    public CampaignStepType StepType { get; set; }

    /// <summary>Same value as <see cref="StepType"/>'s underlying int - kept as a plain column so
    /// ordering/lookup queries do not need to cast the enum.</summary>
    public int StepNumber { get; set; }

    /// <summary>0 for the Initial step; for a follow-up, days after the previous step went out.</summary>
    public int DelayDaysAfterPrevious { get; set; }

    /// <summary>Editor-facing body, e.g. with <c>{{FirstName}}</c> tokens - see <see cref="Messaging.MessageTemplate.BodyText"/>.</summary>
    public string MessageText { get; set; } = string.Empty;

    /// <summary>Must resolve to an Approved template before the campaign can start.</summary>
    public Guid? MessageTemplateId { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<CampaignStepMedia> StepMedia { get; set; } = new List<CampaignStepMedia>();
}
