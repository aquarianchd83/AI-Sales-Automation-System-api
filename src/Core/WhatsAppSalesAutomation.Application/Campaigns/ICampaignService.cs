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

    /// <summary>
    /// Draft or Stopped only. Hard delete. A Draft campaign never has Messages (nothing sends before
    /// Start), so there is nothing to lose; a Stopped one usually does, and those are deleted right
    /// along with it - there is no way to keep a record of what was sent once the campaign itself is
    /// gone. Anything else (Scheduled/Running/Paused) must be Stopped first.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces the step at this StepType's position. Draft or Paused campaigns only.</summary>
    Task<CampaignDto> UpsertStepAsync(Guid campaignId, UpsertCampaignStepRequest request, CancellationToken cancellationToken = default);

    Task<CampaignDto> RemoveStepAsync(Guid campaignId, string stepType, CancellationToken cancellationToken = default);

    /// <summary>Attaches opted-in, non-deleted customers matching the tags and/or ids given. Safe to call repeatedly to grow an audience.</summary>
    Task<SetCampaignAudienceResultDto> SetAudienceAsync(Guid campaignId, SetCampaignAudienceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Callable on Draft, Paused or Stopped. Validates the campaign is sendable (an active Initial
    /// step referencing an Approved template, every active step within the configured media count)
    /// and moves it to Running, or to Scheduled if <c>ScheduledStartAt</c> is in the future. Resuming
    /// a Stopped campaign clears StoppedAt but does not revive the individual CampaignCustomers
    /// StopAsync force-completed - see its remarks.
    /// </summary>
    Task<CampaignDto> StartAsync(Guid campaignId, CancellationToken cancellationToken = default);

    Task<CampaignDto> PauseAsync(Guid campaignId, CancellationToken cancellationToken = default);

    /// <summary>Alias for <see cref="StartAsync"/> - also works on a Stopped campaign, not only Paused.</summary>
    Task<CampaignDto> ResumeAsync(Guid campaignId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Not terminal: a Stopped campaign can be restarted via <see cref="StartAsync"/>/<see cref="ResumeAsync"/>.
    /// Every customer who was AwaitingResponse at the moment of stopping is force-completed here
    /// (StoppedReason = "Campaign stopped") rather than left to resume its follow-up sequence
    /// automatically - resuming re-opens the campaign for sending, not those individual customers'
    /// progress. There is currently no API to revert a specific CampaignCustomer back out of
    /// Completed; SetAudienceAsync will not do it - re-attaching an already-attached customer is
    /// explicitly a no-op there. A customer who should pick back up needs a new campaign, or a
    /// dedicated "reopen" operation this API does not yet expose.
    /// </summary>
    Task<CampaignDto> StopAsync(Guid campaignId, CancellationToken cancellationToken = default);

    Task<CampaignProgressDto> GetProgressAsync(Guid campaignId, CancellationToken cancellationToken = default);

    /// <summary>The roster SetAudienceAsync's counts don't expose - who is actually attached, and
    /// their current status/step. Ordered by LastMessageSentAt desc (most recently active first),
    /// nulls (never sent) last.</summary>
    Task<PagedResult<CampaignAudienceMemberDto>> GetAudienceAsync(Guid campaignId, PagedRequest request, CancellationToken cancellationToken = default);
}
