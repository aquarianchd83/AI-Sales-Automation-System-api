using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Campaigns;

/// <summary>One message in a campaign sequence: the Initial send, or one of any number of follow-ups.</summary>
public class CampaignStep : BaseEntity
{
    public Guid CampaignId { get; set; }

    /// <summary>"Initial" or "FollowUp{N}" - see <see cref="CampaignStepTypeName"/>. Kept as a
    /// plain string, not an enum: a fixed set of members cannot represent an unbounded number
    /// of follow-ups. Always kept in sync with <see cref="StepNumber"/>.</summary>
    public string StepType { get; set; } = CampaignStepTypeName.Initial;

    /// <summary>The real identifier - 0 is Initial, N above 0 is the Nth follow-up. StepType is
    /// derived from this and stored alongside it only so ordering/lookup queries do not need to
    /// re-parse a string.</summary>
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
