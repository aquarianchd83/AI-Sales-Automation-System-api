namespace WhatsAppSalesAutomation.Application.Customers;

public record CustomerDto(
    Guid Id,
    string PhoneNumberE164,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Source,
    string OptInStatus,
    DateTime? OptInTimestamp,
    string? OptInSource,
    /// <summary>The evidence behind the opt-in (message id, call id, form entry). Null for a customer who has not opted in.</summary>
    string? OptInProof,
    /// <summary>"Transactional" or "Marketing" - what the opt-in covers. Null for a customer who has not opted in.</summary>
    string? OptInScope,
    DateTime? OptOutTimestamp,
    /// <summary>Which of the four detection paths produced the opt-out - ExactKeyword, PhrasePattern,
    /// AiDetected or Manual. Null for an opt-out recorded before the field existed. Surfaced because
    /// the field exists to answer a compliance question, and one nobody can read answers nothing.</summary>
    string? OptOutSource,
    string? PreferredLanguage,
    Guid? AssignedAgentId,
    IReadOnlyList<string> Tags,
    DateTime CreatedAt);

/// <summary>One entry of a customer's processing timeline. executionId is the Lead Discovery batch, campaignId the campaign, when either applies.</summary>
public record CustomerHistoryEventDto(
    Guid Id,
    string EventType,
    DateTime OccurredAtUtc,
    string? PreviousStatus,
    string? NewStatus,
    string? Reason,
    Guid? ExecutionId,
    Guid? CampaignId,
    string? CorrelationId,
    string? ProviderReference);

public record CreateCustomerRequest(
    string PhoneNumberE164,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Source,
    string? PreferredLanguage,
    Guid? AssignedAgentId);

public record UpdateCustomerRequest(
    string PhoneNumberE164,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Source,
    string? PreferredLanguage,
    Guid? AssignedAgentId);

public record AddCustomerTagsRequest(IReadOnlyList<string> TagNames);

/// <summary>
/// Records consent. <paramref name="Source"/> is how it was captured (web form, WhatsApp reply,
/// paper form); <paramref name="CapturedAt"/> backdates consent gathered before it reached this
/// system, and defaults to now when omitted.
/// </summary>
/// <summary>Proof and Scope are required: consent with no evidence, or with no stated scope, is not usable.
/// Scope is "Transactional" or "Marketing"; booking-only consent is Transactional and never covers promotions.</summary>
public record OptInCustomerRequest(string Source, DateTime? CapturedAt = null, string? Proof = null, string? Scope = null);

public record BulkDeleteCustomersRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// Result of a bulk delete. Ids that matched nothing are reported rather than failing the whole
/// call, so a caller deleting a selection does not lose the successful deletes to one stale row.
/// An already-soft-deleted customer is invisible to the query filter and so counts as not found.
/// </summary>
public record BulkDeleteCustomersResultDto(
    int RequestedCount,
    int DeletedCount,
    IReadOnlyList<Guid> NotFoundIds);

/// <summary>Adds the same tags to several customers at once.</summary>
public record BulkAddCustomerTagsRequest(IReadOnlyList<Guid> Ids, IReadOnlyList<string> TagNames);

/// <summary>
/// Result of a bulk tag. <paramref name="UpdatedCount"/> is how many customers actually gained a tag - ones that
/// already had every requested tag are found but not counted. Ids that matched nothing (unknown, deleted, or another
/// tenant's) are reported in <paramref name="NotFoundIds"/> rather than failing the call.
/// </summary>
public record BulkAddCustomerTagsResultDto(
    int RequestedCount,
    int UpdatedCount,
    IReadOnlyList<Guid> NotFoundIds,
    IReadOnlyList<string> TagNames);

public record CustomerImportResultDto(
    int TotalRows,
    int ImportedCount,
    int SkippedDuplicateCount,
    int FailedCount,
    IReadOnlyList<CustomerImportRowError> RowErrors);

public record CustomerImportRowError(int RowNumber, string Reason);

/// <summary>The tenant's answer to "is customer consent required?". Required: new customers start WaitingForConsent; not required:
/// PendingOptIn. Either way only an OptedIn customer can be messaged.</summary>
public record CustomerConsentSettingDto(bool ConsentRequired);

public record SetCustomerConsentSettingRequest(bool ConsentRequired);
