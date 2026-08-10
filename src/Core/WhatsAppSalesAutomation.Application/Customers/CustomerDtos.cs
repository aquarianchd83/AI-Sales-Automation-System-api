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
    string? FirstName,
    string? LastName,
    string? Email,
    string? Source,
    string? PreferredLanguage,
    Guid? AssignedAgentId);

public record AddCustomerTagsRequest(IReadOnlyList<string> TagNames);

public record CustomerImportResultDto(
    int TotalRows,
    int ImportedCount,
    int SkippedDuplicateCount,
    int FailedCount,
    IReadOnlyList<CustomerImportRowError> RowErrors);

public record CustomerImportRowError(int RowNumber, string Reason);
