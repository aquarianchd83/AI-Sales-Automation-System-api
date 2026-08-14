namespace WhatsAppSalesAutomation.Application.Campaigns;

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

public record CreateCampaignRequest(string Name, string? Description, DateTime? ScheduledStartAt);

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
