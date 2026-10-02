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
    DateTime? OptOutTimestamp,
    /// <summary>Which of the four detection paths produced the opt-out - ExactKeyword, PhrasePattern,
    /// AiDetected or Manual. Null for an opt-out recorded before the field existed. Surfaced because
    /// the field exists to answer a compliance question, and one nobody can read answers nothing.</summary>
    string? OptOutSource,
    string? PreferredLanguage,
    Guid? AssignedAgentId,
    IReadOnlyList<string> Tags,
    DateTime CreatedAt);

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
public record OptInCustomerRequest(string Source, DateTime? CapturedAt = null);

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
