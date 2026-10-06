using System.Text.Json.Serialization;

namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// The unit a <c>SalesPackage</c>'s duration is quoted in ("3 Months", "1 Year"). Travels as its name ("Months"),
/// which is what the Packages screen sends and expects; the API's default is the number, which made every
/// package save fail with a 400 before this was said.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PackageDurationUnit
{
    Days = 0,
    Weeks = 1,
    Months = 2,
    Years = 3,
}
