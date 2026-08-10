namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Parses a raw CSV/Excel upload into rows. Deliberately does no validation or persistence -
/// that is <c>CustomerService.ImportAsync</c>'s job. Implemented in Infrastructure (CsvHelper/ClosedXML).
/// </summary>
public interface ICustomerImportService
{
    Task<IReadOnlyList<CustomerImportRow>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}

public record CustomerImportRow(
    int RowNumber,
    string PhoneNumber,
    string? FirstName,
    string? LastName,
    string? Email,
    IReadOnlyList<string> Tags);
