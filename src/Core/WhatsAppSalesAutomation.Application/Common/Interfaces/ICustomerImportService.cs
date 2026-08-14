namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Parses a raw CSV/Excel upload into rows. Deliberately does no validation or persistence -
/// that is <c>CustomerService.ImportAsync</c>'s job. Implemented in Infrastructure (CsvHelper/ClosedXML).
/// </summary>
public interface ICustomerImportService
{
    Task<IReadOnlyList<CustomerImportRow>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}

/// <summary>
/// One raw row. Consent fields stay as unparsed strings deliberately - interpreting "yes"/"Y"/
/// "opted-in" and whatever date format the spreadsheet used is a business rule, and business rules
/// live in the Application layer, not the parser.
/// </summary>
public record CustomerImportRow(
    int RowNumber,
    string PhoneNumber,
    string? FirstName,
    string? LastName,
    string? Email,
    IReadOnlyList<string> Tags,
    string? OptInStatus = null,
    string? OptInDate = null,
    string? OptInSource = null);
