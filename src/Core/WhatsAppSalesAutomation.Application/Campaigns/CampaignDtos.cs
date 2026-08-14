namespace WhatsAppSalesAutomation.Application.Campaigns;

/// <summary><paramref name="ScheduledStartAt"/> is India Standard Time (UTC+5:30), not UTC - any
/// offset/'Z' suffix in the request is ignored; send/read the literal digits as IST. See
/// <c>Campaign.ScheduledStartAt</c> for why.</summary>
public record CampaignDto(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    DateTime? ScheduledStartAt,
    Guid CreatedBy,
    DateTime? StartedAt,
    DateTime? StoppedAt,
    int AudienceCount,
    IReadOnlyList<CampaignStepDto> Steps,
    DateTime CreatedAt);

public record CampaignStepDto(
    Guid Id,
    string StepType,
    int StepNumber,
    int DelayDaysAfterPrevious,
    string MessageText,
    Guid? MessageTemplateId,
    string? MessageTemplateName,
    bool IsActive,
    IReadOnlyList<Guid> MediaAssetIds);

/// <summary><paramref name="ScheduledStartAt"/> is interpreted as India Standard Time (UTC+5:30) -
/// see <c>Campaign.ScheduledStartAt</c>.</summary>
public record CreateCampaignRequest(string Name, string? Description, DateTime? ScheduledStartAt);

/// <summary>
/// <paramref name="ScheduledStartAt"/> is India Standard Time - see <c>Campaign.ScheduledStartAt</c>.
/// Passing <c>null</c> while the campaign is Scheduled drops it back to Draft (see
/// <c>CampaignService.UpdateAsync</c>), since a Scheduled campaign with no date would otherwise never
/// come up for promotion again.
/// </summary>
public record UpdateCampaignRequest(string Name, string? Description, DateTime? ScheduledStartAt);

/// <summary><paramref name="StepType"/> is one of Initial, FollowUp1-4; a campaign may have at most one of each.</summary>
public record UpsertCampaignStepRequest(
    string StepType,
    int DelayDaysAfterPrevious,
    string MessageText,
    Guid? MessageTemplateId,
    IReadOnlyList<Guid> MediaAssetIds,
    bool IsActive = true);

/// <summary>
/// Either or both of <paramref name="TagNames"/> / <paramref name="CustomerIds"/> may be given; the
/// audience is their union. Matching is a one-time snapshot into <c>CampaignCustomers</c>, not a
/// live filter - see <c>Campaign.TargetAudienceFilterJson</c>.
/// </summary>
public record SetCampaignAudienceRequest(IReadOnlyList<string>? TagNames, IReadOnlyList<Guid>? CustomerIds);

public record SetCampaignAudienceResultDto(
    int TotalMatched,
    int AddedCount,
    int AlreadyAttachedCount,
    int NotOptedInCount);

public record CampaignProgressDto(Guid CampaignId, int TotalCustomers, IReadOnlyDictionary<string, int> ByStatus);

/// <summary>
/// One customer's row in a campaign's attached audience - the roster SetCampaignAudienceResultDto's
/// counts don't otherwise expose. <paramref name="CurrentStepNumber"/> is -1 until the first message
/// actually sends, matching CampaignCustomer's own default.
/// </summary>
public record CampaignAudienceMemberDto(
    Guid CustomerId,
    string PhoneNumberE164,
    string? FirstName,
    string? LastName,
    string Status,
    int CurrentStepNumber,
    DateTime? LastMessageSentAt,
    DateTime? NextFollowUpDueAt,
    string? StoppedReason);
