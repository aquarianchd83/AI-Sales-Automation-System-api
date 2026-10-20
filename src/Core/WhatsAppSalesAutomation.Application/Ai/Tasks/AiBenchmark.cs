using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.Ai.Tasks;

/// <summary>
/// A small fixed test of the things the product-knowledge engine depends on a provider getting right: answer from a given text, quote the
/// exact supporting sentence, return valid JSON, and - the one that matters most - say "not stated" instead of inventing an answer. It
/// measures the provider with the model the admin has configured, on the admin's own key, so the accuracy shown on the Integrations page is
/// a measurement and not a claim. It is a smoke test of fitness for this job, not a general benchmark of the model.
/// </summary>
public static class AiBenchmark
{
    public const string Passage =
        "Sunrise Eye Clinic is in Pune. The consultation fee is Rs 500 for the first visit. Cataract surgery starts at Rs 25,000 per eye. " +
        "The clinic is open Monday to Saturday, 9 am to 6 pm, and is closed on Sundays. Senior citizens get a free eye check-up until 31 December.";

    public const string System =
        "You answer questions using ONLY the text provided. Reply with one JSON object and nothing else: " +
        "{\"answer\": <short answer, or null>, \"quote\": <the exact sentence copied from the text that supports the answer, or null>}. " +
        "If the text does not state the answer, set both answer and quote to null. Never use outside knowledge and never guess.";

    /// <param name="ExpectedPattern">What the <i>answer</i> itself must match (a regular expression). The quote is never allowed to stand in for the answer.</param>
    public sealed record Case(string Name, string Question, string? ExpectedPattern, bool Answerable);

    public static readonly IReadOnlyList<Case> Cases = new[]
    {
        new Case("Direct fact (fee)", "What is the consultation fee?", @"\b500\b", true),
        new Case("Direct fact (price)", "How much does cataract surgery cost?", @"25,?000", true),
        new Case("Negative fact (Sundays)", "Is the clinic open on Sundays?", @"\b(no|closed|not open)\b", true),
        new Case("Date", "Until when do senior citizens get a free check-up?", @"31\s*dec", true),
        new Case("Not stated (LASIK)", "Does the clinic offer LASIK?", null, false),
        new Case("Trap (other branch)", "What is the consultation fee at the Mumbai branch?", null, false)
    };

    public static string UserPrompt(Case c) => $"TEXT:\n{Passage}\n\nQUESTION: {c.Question}";

    public sealed record CaseResult(string Name, bool Passed, string? Note);

    private static readonly Regex NotStated = new(
        @"not (stated|mentioned|specified|provided|available|found|given)|no information|does not (say|state|mention|specify)|cannot|can't|unknown",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Scores one reply. Pure, so the same reply always scores the same.</summary>
    public static CaseResult Score(Case c, string? reply)
    {
        if (!AiJson.TryExtractObject(reply, out var json))
            return new CaseResult(c.Name, false, "The reply was not valid JSON.");

        using var doc = JsonDocument.Parse(json);
        var answer = Text(doc.RootElement, "answer");
        var quote = Text(doc.RootElement, "quote");

        if (!c.Answerable)
        {
            var admitsGap = string.IsNullOrWhiteSpace(answer) || NotStated.IsMatch(answer);
            if (!admitsGap)
                return new CaseResult(c.Name, false, "It gave an answer the text does not support.");
            if (!string.IsNullOrWhiteSpace(quote))
                return new CaseResult(c.Name, false, "It quoted text for something the text does not state.");
            return new CaseResult(c.Name, true, null);
        }

        if (string.IsNullOrWhiteSpace(answer))
            return new CaseResult(c.Name, false, "It said the text does not state this, but it does.");

        if (!Regex.IsMatch(answer, c.ExpectedPattern!, RegexOptions.IgnoreCase))
            return new CaseResult(c.Name, false, "The answer was not the one in the text.");

        if (string.IsNullOrWhiteSpace(quote))
            return new CaseResult(c.Name, false, "It gave no supporting quote.");

        // The quote has to be real: a sentence that is not in the text is a fabricated citation, which is the failure we most need to catch.
        if (!Normalise(Passage).Contains(Normalise(quote), StringComparison.Ordinal))
            return new CaseResult(c.Name, false, "The quote is not in the text.");

        return new CaseResult(c.Name, true, null);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Lower case with spaces, commas and full stops removed, so "Rs 25,000" and "25000" compare equal.</summary>
    private static string Normalise(string value) => Regex.Replace(value.ToLowerInvariant(), @"[\s,\.]+", string.Empty);
}
