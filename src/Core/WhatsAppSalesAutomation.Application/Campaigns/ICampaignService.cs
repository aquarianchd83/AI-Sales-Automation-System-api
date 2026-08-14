using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Campaigns;

public interface ICampaignService
{
    Task<PagedResult<CampaignDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<CampaignDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CampaignDto> CreateAsync(CreateCampaignRequest request, Guid createdBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Draft, Scheduled or Paused - a Scheduled campaign has not sent anything yet, so its name,
    /// description and start date are still safe to change. Running is excluded: a live campaign's
    /// schedule should not shift under it (by then StartedAt is already fixed and ScheduledStartAt
    /// is moot anyway).
    /// </summary>
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

    /// <summary>The roster SetAudienceAsync's counts don't expose - who is actually attached, and
    /// their current status/step. Ordered by LastMessageSentAt desc (most recently active first),
    /// nulls (never sent) last.</summary>
    Task<PagedResult<CampaignAudienceMemberDto>> GetAudienceAsync(Guid campaignId, PagedRequest request, CancellationToken cancellationToken = default);
}
