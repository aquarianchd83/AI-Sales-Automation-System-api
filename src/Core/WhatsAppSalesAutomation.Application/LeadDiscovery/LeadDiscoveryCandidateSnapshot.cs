using System.Text.Json;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>
/// What the discovery agent reported about one candidate, kept on the candidate's history row whatever became of it - saved,
/// duplicate, rejected or not needed - so a candidate that was not saved can still be looked at afterwards (the "candidates
/// considered" popup). It is the agent's own, unverified claim: a phone or email shown here may be exactly what the rules rejected.
/// Every text is cut to a fixed length, because it is model-written and stored as is.
/// </summary>
public record LeadDiscoveryCandidateSnapshot(
    string BusinessType,
    string? ContactPerson,
    string? Address,
    string? City,
    string? State,
    string? Phone,
    string? PhoneSourceUrl,
    string? Email,
    string? Website,
    string SourceUrl,
    bool? IsIndependentBusiness,
    bool? IsPermanentlyClosed,
    int LeadScore,
    string? ScoreRationale)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static LeadDiscoveryCandidateSnapshot From(DiscoveredBusinessCandidate c) => new(
        Cut(c.BusinessType, 200) ?? string.Empty,
        Cut(c.ContactPerson, 200),
        Cut(c.Address, 500),
        Cut(c.City, 100),
        Cut(c.State, 100),
        Cut(c.Phone, 50),
        Cut(c.PhoneSourceUrl, 1000),
        Cut(c.Email, 254),
        Cut(c.Website, 1000),
        Cut(c.SourceUrl, 1000) ?? string.Empty,
        c.IsIndependentBusiness,
        c.IsPermanentlyClosed,
        Math.Clamp(c.LeadScore, 0, 100),
        Cut(c.ScoreRationale, 1000));

    public static string Serialize(DiscoveredBusinessCandidate candidate) => JsonSerializer.Serialize(From(candidate), Json);

    /// <summary>The snapshot stored on a row, or null when there is none or it cannot be read - a row from before candidates were kept,
    /// or damaged text, must never make the execution unreadable.</summary>
    public static LeadDiscoveryCandidateSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LeadDiscoveryCandidateSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Cut(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
