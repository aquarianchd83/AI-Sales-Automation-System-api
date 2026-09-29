using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Campaigns;

/// <summary><paramref name="ScheduledStartAt"/> is this tenant's own local time (Tenant.Timezone,
/// defaulting to India Standard Time), not UTC - any offset/'Z' suffix in the request is ignored;
/// send/read the literal digits as the tenant's own wall clock. See <c>Campaign.ScheduledStartAt</c>
/// for why.</summary>
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

/// <summary><paramref name="ScheduledStartAt"/> is interpreted as the tenant's own local time -
/// see <c>Campaign.ScheduledStartAt</c>.</summary>
public record CreateCampaignRequest(string Name, string? Description, DateTime? ScheduledStartAt);

/// <summary>
/// <paramref name="ScheduledStartAt"/> is the tenant's own local time - see
/// <c>Campaign.ScheduledStartAt</c>. Passing <c>null</c> while the campaign is Scheduled drops it
/// back to Draft (see
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

/// <summary>
/// Query for GET campaigns/{id}/history. <see cref="PagedRequest.Search"/> matches the customer's
/// name/phone; <see cref="Status"/>/<see cref="StepNumber"/>/<see cref="TemplateName"/>/
/// <see cref="From"/>/<see cref="To"/> are additive column filters. <see cref="Status"/> is a
/// Message.Status (MessageStatus) name. From/To bound Message.CreatedAt by calendar day
/// (inclusive), regardless of the time-of-day given.
/// </summary>
public record CampaignHistoryQuery : PagedRequest
{
    public string? Status { get; init; }
    public int? StepNumber { get; init; }
    public string? TemplateName { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

/// <summary>One message this campaign has sent - the per-send detail behind
/// CampaignAudienceMemberDto's roster.</summary>
public record CampaignMessageHistoryEntryDto(
    Guid MessageId,
    Guid CustomerId,
    string PhoneNumberE164,
    string? FirstName,
    string? LastName,
    int? StepNumber,
    string? TemplateName,
    string? Text,
    string Status,
    string? FailureReason,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    DateTime? ReadAt,
    DateTime CreatedAt);

/// <summary>Delivery outcome of one campaign step across the whole audience - who has it, who is
/// still due it, who never will. <see cref="Recipients"/> is the audience size; every other count
/// is a slice of it, so Queued+Sent+Delivered+Read+Failed+Upcoming+WillNotReceive == Recipients.</summary>
public record CampaignStepDeliverySummaryDto(
    int StepNumber,
    string StepType,
    string? TemplateName,
    bool IsActive,
    int Recipients,
    int Queued,
    int Sent,
    int Delivered,
    int Read,
    int Failed,
    int Upcoming,
    int WillNotReceive);

/// <summary>Query for GET campaigns/{id}/steps/{stepNumber}/recipients. <see cref="Outcome"/> is one of
/// the <see cref="CampaignStepOutcome"/> names.</summary>
public record CampaignStepRecipientQuery : PagedRequest
{
    public string? Outcome { get; init; }
}

/// <summary>One audience member's outcome for one step. <see cref="MessageId"/> is set once a message
/// exists for the step (the id to resend when <see cref="Outcome"/> is Failed).
/// <see cref="DueAt"/> is when an Upcoming step becomes eligible (null = next send run).
/// <see cref="Note"/> says why a WillNotReceive recipient will not get it.</summary>
public record CampaignStepRecipientDto(
    Guid CustomerId,
    string PhoneNumberE164,
    string? FirstName,
    string? LastName,
    string Outcome,
    Guid? MessageId,
    string? FailureReason,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    DateTime? ReadAt,
    DateTime? DueAt,
    string? Note);

public static class CampaignStepOutcome
{
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Delivered = "Delivered";
    public const string Read = "Read";
    public const string Failed = "Failed";
    public const string Upcoming = "Upcoming";
    public const string WillNotReceive = "WillNotReceive";
}
