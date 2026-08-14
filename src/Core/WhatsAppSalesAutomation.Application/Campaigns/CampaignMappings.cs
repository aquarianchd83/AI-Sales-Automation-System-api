using WhatsAppSalesAutomation.Domain.Entities.Campaigns;

namespace WhatsAppSalesAutomation.Application.Campaigns;

public static class CampaignMappings
{
    public static CampaignStepDto ToDto(this CampaignStep step, string? templateName) => new(
        step.Id,
        step.StepType.ToString(),
        step.StepNumber,
        step.DelayDaysAfterPrevious,
        step.MessageText,
        step.MessageTemplateId,
        templateName,
        step.IsActive,
        step.StepMedia.Select(m => m.MediaAssetId).ToList());

    public static CampaignDto ToDto(this Campaign campaign, int audienceCount, IReadOnlyDictionary<Guid, string> templateNamesById) => new(
        campaign.Id,
        campaign.Name,
        campaign.Description,
        campaign.Status.ToString(),
        campaign.ScheduledStartAt,
        campaign.CreatedBy,
        campaign.StartedAt,
        campaign.StoppedAt,
        audienceCount,
        campaign.Steps
            .OrderBy(s => s.StepNumber)
            .Select(s => s.ToDto(s.MessageTemplateId.HasValue && templateNamesById.TryGetValue(s.MessageTemplateId.Value, out var name) ? name : null))
            .ToList(),
        campaign.CreatedAt);
}
