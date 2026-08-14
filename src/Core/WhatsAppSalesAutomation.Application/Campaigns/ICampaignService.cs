using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Campaigns;

public interface ICampaignService
{
    Task<PagedResult<CampaignDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<CampaignDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CampaignDto> CreateAsync(CreateCampaignRequest request, Guid createdBy, CancellationToken cancellationToken = default);

    /// <summary>Draft campaigns only - a live campaign's name/schedule should not shift under it.</summary>
    Task<CampaignDto> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken = default);

    /// <summary>Draft campaigns only. Hard delete - nothing has been sent yet, so there is no history to preserve.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces the step at this StepType's position. Draft or Paused campaigns only.</summary>
    Task<CampaignDto> UpsertStepAsync(Guid campaignId, UpsertCampaignStepRequest request, CancellationToken cancellationToken = default);

    Task<CampaignDto> RemoveStepAsync(Guid campaignId, string stepType, CancellationToken cancellationToken = default);

    /// <summary>Attaches opted-in, non-deleted customers matching the tags and/or ids given. Safe to call repeatedly to grow an audience.</summary>
    Task<SetCampaignAudienceResultDto> SetAudienceAsync(Guid campaignId, SetCampaignAudienceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the campaign is sendable (an active Initial step referencing an Approved template,
    /// every active step within the configured media count) and moves it to Running, or to Scheduled
    /// if <c>ScheduledStartAt</c> is in the future.
    /// </summary>
    Task<CampaignDto> StartAsync(Guid campaignId, CancellationToken cancellationToken = default);

    Task<CampaignDto> PauseAsync(Guid campaignId, CancellationToken cancellationToken = default);

    Task<CampaignDto> ResumeAsync(Guid campaignId, CancellationToken cancellationToken = default);

    /// <summary>Terminal - a stopped campaign cannot be resumed, only recreated.</summary>
    Task<CampaignDto> StopAsync(Guid campaignId, CancellationToken cancellationToken = default);

    Task<CampaignProgressDto> GetProgressAsync(Guid campaignId, CancellationToken cancellationToken = default);
}
