using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Ai;

public class AiReplyValidator : IAiReplyValidator
{
    /// <summary>A WhatsApp reply longer than this is not a reply, it is a document - and the prompt
    /// asks for two or three sentences. Hitting this means the model ignored the instruction badly
    /// enough that a human should look.</summary>
    public const int MaxResponseChars = 1200;

    /// <summary>Terms that only appear in a reply if the agent has started describing its own
    /// machinery. Deliberately a short list of words a salesperson would not use in this sense, and
    /// matched on word boundaries - "our knowledge base of 200 properties" is a sentence a property
    /// dealer might genuinely write, which is why hitting this escalates for a human to look at
    /// instead of silently redacting.</summary>
    private static readonly string[] InternalTerms =
    {
        "knowledge base", "knowledge-base", "retrieval", "embedding", "embeddings",
        "system prompt", "prompt", "tool call", "function call", "chunk",
        "lead score", "confidence score", "qualification field", "my instructions",
        "language model", "as an ai"
    };

    /// <summary>Words that would only be in a reply if the agent were narrating its own ranking of the
    /// customer. Checked separately because these are blocked only when the tenant has not opted in.</summary>
    private static readonly string[] ScoreTerms = { "lead score", "score", "rating", "ranked", "ranking", "hot lead" };

    private static readonly Regex NumberPattern = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);

    public ValidatedReply Validate(AiReplyResult result, AiConversationContext context)
    {
        var failures = new List<ValidationFailure>();
        var text = (result.ResponseText ?? string.Empty).Trim();

        // ── V-1: intent must be one this system understands ──────────────────────────────────
        var intent = Enum.TryParse<CustomerIntent>(result.DetectedIntent, ignoreCase: true, out var parsedIntent)
            ? parsedIntent.ToString()
            : Fail(failures, "UnknownIntent", $"'{result.DetectedIntent}' is not a CustomerIntent.", blocking: false,
                fallback: CustomerIntent.Unknown.ToString());

        // ── V-2 / V-3: extracted fields must belong to this tenant's schema ───────────────────
        var schemaKeys = context.SchemaFields
            .Select(f => f.FieldKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var acceptedFields = new List<AiExtractedField>();
        foreach (var field in result.ExtractedFields ?? Array.Empty<AiExtractedField>())
        {
            if (string.IsNullOrWhiteSpace(field.FieldKey) || !schemaKeys.Contains(field.FieldKey))
            {
                // Dropped, not blocking: the rest of the turn is still usable, and a reply is not worse
                // for the model having invented a field name alongside it.
                failures.Add(new ValidationFailure(
                    "UnknownField", $"'{field.FieldKey}' is not in this tenant's schema.", Blocking: false));
                continue;
            }

            if (string.IsNullOrWhiteSpace(field.Value))
                continue;

            acceptedFields.Add(field);
        }

        // ── V-5: asked_field_key must be one we actually offered ─────────────────────────────
        string? askedFieldKey = result.AskedFieldKey;
        if (!string.IsNullOrWhiteSpace(askedFieldKey)
            && !context.FieldsToAsk.Any(f => string.Equals(f.FieldKey, askedFieldKey, StringComparison.OrdinalIgnoreCase)))
        {
            // Audit-only. The reply may well be fine; we just cannot trust this label when measuring
            // which questions actually work.
            failures.Add(new ValidationFailure(
                "UnofferedField", $"Reported asking '{askedFieldKey}', which was not offered.", Blocking: false));
            askedFieldKey = null;
        }

        // ── V-6: citations must be chunks this turn was actually given ───────────────────────
        var offeredChunks = context.GroundingChunks.Select(c => c.ChunkId).ToHashSet();
        var citedIds = (result.CitedChunkIds ?? Array.Empty<Guid>()).Where(offeredChunks.Contains).ToList();

        if ((result.CitedChunkIds?.Count ?? 0) != citedIds.Count)
        {
            failures.Add(new ValidationFailure(
                "HallucinatedCitation", "Cited a chunk id that was not supplied to this turn.", Blocking: false));
        }

        // ── V-7: an empty reply cannot be sent ───────────────────────────────────────────────
        if (text.Length == 0)
        {
            failures.Add(new ValidationFailure(
                "EmptyResponse", "The model returned no reply text.", Blocking: true));
        }

        // ── V-10: length ─────────────────────────────────────────────────────────────────────
        if (text.Length > MaxResponseChars)
        {
            failures.Add(new ValidationFailure(
                "ResponseTooLong", $"{text.Length} characters; the limit is {MaxResponseChars}.", Blocking: true));
        }

        // ── V-8: the agent must not describe its own machinery ───────────────────────────────
        var leaked = InternalTerms.FirstOrDefault(term => ContainsPhrase(text, term));
        if (leaked is not null)
        {
            failures.Add(new ValidationFailure(
                "InternalTermLeak", $"Reply mentions '{leaked}'.", Blocking: true));
        }

        // ── V-9: never disclose how the customer is assessed, unless the tenant allows it ─────
        if (!context.Business.MayDiscloseLeadScore)
        {
            var scoreTerm = ScoreTerms.FirstOrDefault(term => ContainsPhrase(text, term));
            if (scoreTerm is not null)
            {
                failures.Add(new ValidationFailure(
                    "ScoreDisclosure", $"Reply mentions '{scoreTerm}' and this tenant does not allow it.", Blocking: true));
            }
        }

        // ── V-2 (numeric grounding): every number must be traceable ──────────────────────────
        var ungrounded = UngroundedNumbers(text, context, result);
        if (ungrounded.Count > 0)
        {
            failures.Add(new ValidationFailure(
                "UngroundedNumber",
                $"Reply states {string.Join(", ", ungrounded)} with nothing in the knowledge or the "
                + "conversation to support it.",
                Blocking: true));
        }

        return new ValidatedReply(
            CanSend: !failures.Any(f => f.Blocking),
            Failures: failures,
            ResponseText: text,
            DetectedIntent: intent,
            ConfidenceScore: Math.Clamp(result.ConfidenceScore, 0, 1),
            ExtractedFields: acceptedFields,
            CitedChunkIds: citedIds,
            UpdatedSummary: result.UpdatedSummary ?? string.Empty,
            BuyingIntentDetected: result.BuyingIntentDetected,
            HumanRequested: result.HumanRequested,
            OptOutRequested: result.OptOutRequested,
            AskedFieldKey: askedFieldKey,
            DetectedLanguage: result.DetectedLanguage,
            AgentNote: Truncate(result.AgentNote, 500));
    }

    /// <summary>
    /// Finds numbers in the reply that appear nowhere the agent was allowed to get them from.
    ///
    /// This is the single most useful check here. A hallucinated price, date or quantity is the most
    /// expensive kind of wrong answer a sales agent can give, and it is also the easiest to detect:
    /// the number either came from the knowledge, from the customer, or from nowhere.
    ///
    /// Single digits and short ordinals are ignored. "Step 2", "3 options" and "two or three days"
    /// would otherwise fail constantly, and nothing a customer would act on is a bare single digit.
    /// </summary>
    private static List<string> UngroundedNumbers(string text, AiConversationContext context, AiReplyResult result)
    {
        var sources = new List<string>(capacity: context.GroundingChunks.Count + context.RecentHistory.Count + 4);
        sources.AddRange(context.GroundingChunks.Select(c => c.Text));
        sources.AddRange(context.RecentHistory.Select(t => t.Text));
        sources.AddRange(context.KnownFields.Select(f => f.RawValue));
        sources.Add(context.InboundMessageText);
        sources.Add(context.ExistingSummary ?? string.Empty);

        foreach (var field in result.ExtractedFields ?? Array.Empty<AiExtractedField>())
            sources.Add(field.Value);

        var haystack = string.Join(" ", sources);
        var ungrounded = new List<string>();

        foreach (Match match in NumberPattern.Matches(text))
        {
            var raw = match.Value;
            var digits = raw.Replace(",", string.Empty);

            // Anything under three digits is ordinary prose, not a claim.
            if (digits.Replace(".", string.Empty).Length < 3)
                continue;

            // Compared with separators stripped from both sides, so "1,00,000" in the reply matches
            // "100000" in the source - the same amount written the way an Indian customer writes it.
            if (haystack.Contains(raw, StringComparison.OrdinalIgnoreCase)
                || StripSeparators(haystack).Contains(digits, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ungrounded.Contains(raw))
                ungrounded.Add(raw);
        }

        return ungrounded;
    }

    private static string StripSeparators(string text) => text.Replace(",", string.Empty).Replace(" ", string.Empty);

    /// <summary>Word-boundary match, so "prompt" does not fire on "promptly" and "score" does not fire
    /// on "scorecard".</summary>
    private static bool ContainsPhrase(string text, string phrase)
    {
        var index = 0;
        while ((index = text.IndexOf(phrase, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var after = index + phrase.Length;
            var afterOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);

            if (beforeOk && afterOk)
                return true;

            index = after;
        }

        return false;
    }

    private static string Fail(
        List<ValidationFailure> failures, string code, string detail, bool blocking, string fallback)
    {
        failures.Add(new ValidationFailure(code, detail, blocking));
        return fallback;
    }

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null
        : text.Length <= max ? text.Trim()
        : text.Trim()[..max];
}
