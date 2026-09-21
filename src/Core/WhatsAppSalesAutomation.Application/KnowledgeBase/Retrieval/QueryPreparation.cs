using System.Text;
using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;

/// <summary>One query sent to retrieval, with the weight its results carry in fusion.</summary>
public sealed record ExpandedQuery(string Name, string Text, double Weight);

/// <summary>
/// Turns a raw ticket message into the text retrieval actually searches with (§K.2).
///
/// Everything here is for the RETRIEVAL QUERY only. The ticket's own text is never altered - masking
/// a phone number out of what is searched must not mask it out of what the agent reads.
/// </summary>
public static class QueryNormalizer
{
    /// <summary>Above this the message is condensed rather than embedded whole. A very long message
    /// embeds as an average of everything in it, which is close to nothing in particular.</summary>
    public const int MaxQueryTokens = 1500;

    private static readonly Regex QuotedReplyMarker = new(
        @"^\s*(On\s.+\swrote:|-{2,}\s*Original Message\s*-{2,}|_{5,}|From:\s.+@.+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GuidPattern = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhoneCandidate = new(@"(?<![\w.])\+?\d[\d\s().-]{8,}\d(?!\w)", RegexOptions.Compiled);

    public static string Normalize(string? raw, ITokenCounter tokens)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Normalize(NormalizationForm.FormC).Trim();

        // Everything after "On <date> wrote:" is the previous message quoted back, not the question.
        var marker = QuotedReplyMarker.Match(text);
        if (marker.Success && marker.Index > 0)
            text = text[..marker.Index];

        text = string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith('>')));

        text = MaskPii(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return tokens.Count(text) > MaxQueryTokens ? Condense(text, tokens) : text;
    }

    /// <summary>Phone numbers, emails and ids add noise to an embedding and put personal data into a
    /// cache key. The digit-count check keeps error codes (131047) and versions (2.10.0) intact.</summary>
    public static string MaskPii(string text)
    {
        text = EmailPattern.Replace(text, "<EMAIL>");
        text = GuidPattern.Replace(text, "<ID>");
        return PhoneCandidate.Replace(text, m => m.Value.Count(char.IsDigit) >= 10 ? "<PHONE>" : m.Value);
    }

    /// <summary>Keeps the opening and the closing of a long message, where the question and the
    /// actual ask usually are, and drops the middle.</summary>
    private static string Condense(string text, ITokenCounter tokens)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?।])\s+").Where(s => s.Length > 0).ToList();
        var head = new List<string>();
        var tail = new List<string>();
        var used = 0;

        foreach (var s in sentences)
        {
            var cost = tokens.Count(s);
            if (used + cost > MaxQueryTokens * 6 / 10) break;
            head.Add(s);
            used += cost;
        }

        var tailBudget = MaxQueryTokens * 3 / 10;
        var tailUsed = 0;
        for (var i = sentences.Count - 1; i >= head.Count; i--)
        {
            var cost = tokens.Count(sentences[i]);
            if (tailUsed + cost > tailBudget) break;
            tail.Insert(0, sentences[i]);
            tailUsed += cost;
        }

        return string.Join(" ", head.Concat(tail));
    }
}

/// <summary>
/// The three queries (§K.2): what the customer said, a clean restatement, and the conversation so far.
///
/// Three because each finds what the others miss. The verbatim query has their real words; the
/// canonical one bridges a customer's phrasing to the article's ("quota khatam" to "credits
/// exhausted"); the contextual one rescues a follow-up like "and what about that?" that means nothing
/// alone. Their rankings are fused, so an article any one of them finds can surface.
/// </summary>
public static class QueryExpander
{
    public const double VerbatimWeight = 1.0;
    public const double CanonicalWeight = 0.8;
    public const double ContextualWeight = 0.6;

    private static readonly Regex ErrorCode = new(@"\b\d{5,7}\b", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"\bv?\d+\.\d+(?:\.\d+)?\b", RegexOptions.Compiled);

    public static IReadOnlyList<ExpandedQuery> Expand(
        string normalized,
        IReadOnlyList<string> conversationContext,
        SupportIntent? intent,
        ProductModule? module)
    {
        if (normalized.Length == 0)
            return Array.Empty<ExpandedQuery>();

        var queries = new List<ExpandedQuery> { new("Q1-verbatim", normalized, VerbatimWeight) };

        var canonical = BuildCanonical(normalized, intent, module);
        if (canonical is not null && !canonical.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            queries.Add(new ExpandedQuery("Q2-canonical", canonical, CanonicalWeight));

        // Only when there IS earlier conversation: otherwise it is Q1 again and would just double-
        // count the same ranking.
        if (conversationContext.Count > 0)
        {
            var recent = string.Join(" ", conversationContext.TakeLast(3).Select(t => t.Trim()));
            var contextual = (recent + " " + normalized).Trim();
            if (contextual.Length > 800)
                contextual = contextual[^800..];

            queries.Add(new ExpandedQuery("Q3-contextual", contextual, ContextualWeight));
        }

        return queries;
    }

    /// <summary>A clean restatement built without a model: intent phrase, module, and the exact tokens
    /// that matter most in support - error codes and versions - lifted out of the noise. Null when
    /// there is nothing to add over the verbatim query.</summary>
    internal static string? BuildCanonical(string normalized, SupportIntent? intent, ProductModule? module)
    {
        var parts = new List<string>();

        if (intent is { } i && i != SupportIntent.Unknown)
            parts.Add(Humanize(i.ToString()));

        var effectiveModule = module ?? (intent is { } i2 ? SupportIntentRules.DefaultModule(i2) : null);
        if (effectiveModule is { } m)
            parts.Add(Humanize(m.ToString()));

        var entities = ErrorCode.Matches(normalized).Select(x => x.Value)
            .Concat(Version.Matches(normalized).Select(x => x.Value))
            .Distinct().ToList();
        if (entities.Count > 0)
            parts.Add(string.Join(" ", entities));

        if (parts.Count == 0)
            return null;

        var head = normalized.Length > 300 ? normalized[..300] : normalized;
        return string.Join(" | ", parts) + " | " + head;
    }

    private static string Humanize(string pascal) => Regex.Replace(pascal, "([a-z])([A-Z])", "$1 $2");
}
