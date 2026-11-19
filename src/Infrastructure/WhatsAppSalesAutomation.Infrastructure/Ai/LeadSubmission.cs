using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>Turns the arguments of a model's <c>submit_leads</c> call (the same schema for every provider)
/// into candidates. Unverified: LeadQualification decides which of them are leads.</summary>
internal static class LeadSubmission
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<DiscoveredBusinessCandidate> Parse(JsonNode? input)
    {
        var submission = input?.Deserialize<SubmitLeadsInput>(JsonOptions);

        return (submission?.Candidates ?? new List<CandidatePayload>())
            .Select(c => new DiscoveredBusinessCandidate(
                c.BusinessName ?? string.Empty,
                c.BusinessType ?? string.Empty,
                c.ContactPerson,
                c.Address,
                c.City,
                c.State,
                c.Phone,
                c.PhoneSourceUrl,
                c.Email,
                c.Website,
                c.SourceUrl ?? string.Empty,
                c.IsIndependentBusiness,
                c.IsPermanentlyClosed,
                c.LeadScore,
                c.ScoreRationale))
            .ToList();
    }

    /// <summary>Records what a round submitted. An empty or short submission is logged as a warning with the model's own
    /// explanation (the schema's note field), so an empty run can be explained without re-running it. The note is
    /// model-written text, so it is length-limited; no prompt, page or key is logged.</summary>
    public static void LogOutcome(ILogger logger, string provider, int submitted, int maximum, JsonNode? input)
    {
        var note = NoteOf(input) ?? "none given";

        if (submitted < maximum)
        {
            logger.LogWarning("{Provider} lead discovery submitted {Submitted} of at most {Maximum} candidate(s). Agent's note: {Note}",
                provider, submitted, maximum, note);
        }
        else
        {
            logger.LogInformation("{Provider} lead discovery submitted {Submitted} candidate(s). Agent's note: {Note}", provider, submitted, note);
        }
    }

    /// <summary>The agent's own explanation in its submission (the schema's note field), trimmed and length-limited; null when it gave none.</summary>
    public static string? NoteOf(JsonNode? input)
    {
        var note = input?["note"] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;
        return string.IsNullOrEmpty(note) ? null : note.Length > MaxNoteLength ? note[..MaxNoteLength] : note;
    }

    /// <summary>What a round that ended without reporting anything is recorded as.</summary>
    public const string NoSubmissionNote = "The search ended without reporting any businesses (it used up its turns before finishing).";

    private const int MaxNoteLength = 600;

    private sealed class SubmitLeadsInput
    {
        public List<CandidatePayload>? Candidates { get; set; }
    }

    private sealed class CandidatePayload
    {
        public string? BusinessName { get; set; }
        public string? BusinessType { get; set; }
        public string? ContactPerson { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? Phone { get; set; }
        public string? PhoneSourceUrl { get; set; }
        public string? Email { get; set; }
        public string? Website { get; set; }
        public string? SourceUrl { get; set; }
        public bool? IsIndependentBusiness { get; set; }
        public bool? IsPermanentlyClosed { get; set; }
        public int LeadScore { get; set; }
        public string? ScoreRationale { get; set; }
    }
}
