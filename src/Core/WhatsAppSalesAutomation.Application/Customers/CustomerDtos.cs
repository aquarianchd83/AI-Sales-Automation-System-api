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

public record CustomerImportResultDto(
    int TotalRows,
    int ImportedCount,
    int SkippedDuplicateCount,
    int FailedCount,
    IReadOnlyList<CustomerImportRowError> RowErrors);

public record CustomerImportRowError(int RowNumber, string Reason);
