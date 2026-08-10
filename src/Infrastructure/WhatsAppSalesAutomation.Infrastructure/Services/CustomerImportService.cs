using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Services;

/// <summary>
/// Parses customer import files. Expected columns (case/space-insensitive header matching):
/// PhoneNumber (required, E.164), FirstName, LastName, Email, Tags (comma/semicolon separated).
/// </summary>
public class CustomerImportService : ICustomerImportService
{
    public Task<IReadOnlyList<CustomerImportRow>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".csv" => ParseCsvAsync(fileStream, cancellationToken),
            ".xlsx" or ".xls" => Task.FromResult(ParseExcel(fileStream)),
            _ => throw new NotSupportedException($"Unsupported import file type '{extension}'. Use .csv or .xlsx.")
        };
    }

    private static async Task<IReadOnlyList<CustomerImportRow>> ParseCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        var rows = new List<CustomerImportRow>();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant().Replace(" ", string.Empty)
        };

        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();

        var rowNumber = 1;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;

            rows.Add(MapRow(
                rowNumber,
                GetField(csv, "phonenumber"),
                GetField(csv, "firstname"),
                GetField(csv, "lastname"),
                GetField(csv, "email"),
                GetField(csv, "tags")));
        }

        return rows;
    }

    private static string? GetField(CsvReader csv, string name) =>
        csv.TryGetField<string>(name, out var value) ? value : null;

    private static IReadOnlyList<CustomerImportRow> ParseExcel(Stream stream)
    {
        var rows = new List<CustomerImportRow>();

        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.First();

        var columnMap = new Dictionary<string, int>();
        foreach (var cell in worksheet.Row(1).CellsUsed())
        {
            var key = cell.GetString().Trim().ToLowerInvariant().Replace(" ", string.Empty);
            columnMap[key] = cell.Address.ColumnNumber;
        }

        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= lastRow; r++)
        {
            var row = worksheet.Row(r);
            if (row.IsEmpty()) continue;

            string? Get(string col) => columnMap.TryGetValue(col, out var idx)
                ? row.Cell(idx).GetString().Trim()
                : null;

            rows.Add(MapRow(r, Get("phonenumber"), Get("firstname"), Get("lastname"), Get("email"), Get("tags")));
        }

        return rows;
    }

    private static CustomerImportRow MapRow(int rowNumber, string? phone, string? firstName, string? lastName, string? email, string? tags)
    {
        var tagList = string.IsNullOrWhiteSpace(tags)
            ? Array.Empty<string>()
            : tags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new CustomerImportRow(rowNumber, phone?.Trim() ?? string.Empty, firstName, lastName, email, tagList);
    }
}
