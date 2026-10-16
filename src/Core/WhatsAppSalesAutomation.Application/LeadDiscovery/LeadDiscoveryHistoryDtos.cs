using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>Query for GET lead-discovery/history. Paged by processing date: a page holds up to PageSize dates,
/// each with all of its executions. <see cref="Status"/> is a LeadDiscoveryExecutionStatus name.</summary>
public record LeadDiscoveryHistoryQuery : PagedRequest
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? Status { get; init; }
}

/// <summary>One execution in Lead Discovery History. Statuses are enum names - see LeadDiscoveryStatuses.
/// <paramref name="EstimatedCostLocal"/> is the linked run's cost in the tenant's own currency (zero when the
/// execution did no research - a retry, or a run skipped before it started); <paramref name="CurrencyCode"/>/
/// <paramref name="CurrencySymbol"/> are the tenant's currency regardless, so a zero still reads correctly.</summary>
public record LeadDiscoveryExecutionSummaryDto(
    Guid Id,
    DateTime ProcessingDate,
    Guid LeadDiscoveryProfileId,
    string ProfileName,
    string Trigger,
    string Status,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    string LockStatus,
    bool QuotaExhausted,
    int CustomersDiscovered,
    int CustomersCreated,
    int CustomersDuplicate,
    int CustomersInvalid,
    int CustomersFailed,
    int CustomersSkipped,
    bool AutoCampaignConfigured,
    Guid? AutoCampaignId,
    Guid? ReferredCampaignId,
    string? ReferredCampaignName,
    Guid? GeneratedCampaignId,
    string? GeneratedCampaignName,
    string CampaignStatus,
    string? CampaignNote,
    string TemplateStatus,
    string MappingStatus,
    int MappingsEligible,
    int MappingsCreated,
    int MappingsExisting,
    int MappingsFailed,
    Guid RootExecutionId,
    Guid? RetryOfExecutionId,
    Guid? SupersededByExecutionId,
    int RetryCount,
    DateTime? LastRetryAtUtc,
    string? FailedStep,
    string? ErrorMessage,
    string? NextRetryInfo,
    bool CanRetry,
    decimal EstimatedCostLocal,
    string CurrencyCode,
    string CurrencySymbol);

/// <summary>One processing date and every execution that belongs to it, newest first.</summary>
public record LeadDiscoveryHistoryDayDto(DateTime ProcessingDate, IReadOnlyList<LeadDiscoveryExecutionSummaryDto> Executions);

public record LeadDiscoveryExecutionCustomerDto(
    Guid Id,
    Guid? CustomerId,
    Guid? DiscoveredLeadId,
    string CustomerName,
    string? Phone,
    string Status,
    bool IsInvalid,
    string? ErrorMessage,
    string? ErrorDetails,
    Guid? RetriedFromId,
    DateTime? ProcessedAtUtc,
    /// <summary>NotChecked, Pending, Pass, Fail or Error - the DND / outreach-eligibility check. Fail is a rejected number, Error a check that could not be completed.</summary>
    string VerificationStatus = "NotChecked",
    string? VerificationReasonCode = null,
    string? VerificationReason = null,
    DateTime? VerifiedAtUtc = null,
    /// <summary>The name on the CRM customer created for this row (the named contact, else the business) - null when no customer exists, or it was since deleted.</summary>
    string? CrmCustomerName = null,
    /// <summary>The campaign this execution generated, when the row's customer was added to it; null otherwise.</summary>
    string? CampaignName = null,
    /// <summary>Pending, Submitted, Delivered or Failed - the SMS inviting the customer to chat on WhatsApp; null when none was sent. Submitted is not delivered.</summary>
    string? ConsentSmsStatus = null,
    string? ConsentSmsFailureReason = null,
    /// <summary>When the customer's own WhatsApp message answered the invitation and consent was recorded; null until then.</summary>
    DateTime? ConsentRecordedAtUtc = null,
    /// <summary>Why no consent SMS went to this row, in words - "Not sent: ..." - so the column is never an unexplained blank. Null when an SMS was sent.</summary>
    string? ConsentSmsNote = null,
    /// <summary>True when the SMS was not sent because the consent SMS switch in the discovery profile is Off, so the screen can link to where it is turned on.</summary>
    bool ConsentSmsNeedsSetting = false);

public record LeadDiscoveryExecutionTemplateDto(
    Guid Id,
    Guid? TemplateId,
    string TemplateName,
    int Sequence,
    int DelayDaysAfterPrevious,
    Guid? CampaignStepId,
    string Status,
    string? ErrorMessage);

public record LeadDiscoveryLockTransitionDto(
    Guid Id,
    string FromStatus,
    string ToStatus,
    DateTime TransitionAtUtc,
    string? LockTokenReference,
    string? OwnerInstanceId,
    string? Reason,
    string? Error);

/// <summary>Everything recorded about one execution.</summary>
/// <param name="Research">What this execution's AI research used and cost - null when it did no research: a
/// retry, a skipped or blocked run, a run stopped before its first round, or one recorded before runs were linked
/// to executions. <see cref="CurrencyCode"/>/<see cref="CurrencySymbol"/> are the tenant's currency, for
/// Research.EstimatedCostLocal.</param>
/// <summary>How the execution's numbers came out of the DND / outreach-eligibility check. Pending and Error are numbers still to be
/// checked, so a retry would ask again; Failed are rejected numbers, which never became customers; NotChecked rows were never submitted
/// (invalid, duplicate, or the check was off). The five add up to the rows the execution recorded.</summary>
public record LeadDiscoveryVerificationCountsDto(int Passed, int Failed, int Pending, int Error, int NotChecked);

public record LeadDiscoveryExecutionDetailDto(
    LeadDiscoveryExecutionSummaryDto Execution,
    string LockKey,
    string? LockTokenReference,
    string? LockOwnerInstanceId,
    DateTime? LockAcquiredAtUtc,
    DateTime? LockExpiresAtUtc,
    DateTime? LockLastRenewedAtUtc,
    string? ErrorDetails,
    string? Summary,
    IReadOnlyList<LeadDiscoveryExecutionCustomerDto> Customers,
    IReadOnlyList<LeadDiscoveryExecutionTemplateDto> Templates,
    IReadOnlyList<LeadDiscoveryLockTransitionDto> LockTransitions,
    LeadDiscoveryRunDto? Research,
    string CurrencyCode,
    string CurrencySymbol,
    LeadDiscoveryVerificationCountsDto VerificationCounts);

/// <summary>A manual retry was queued; it runs as a new execution with a new Execution ID.</summary>
public record LeadDiscoveryRetryQueuedDto(Guid ExecutionId, string BackgroundJobId);
