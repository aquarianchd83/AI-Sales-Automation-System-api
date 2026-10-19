using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <summary>One word of a text and where it sits, so a quotation can be cut back out of the original.</summary>
public readonly record struct TextToken(string Word, int Start, int End);

/// <summary>Words, numbers and the common-sense stop list shared by the evidence rules. Letters and digits of any script count as words.</summary>
public static class EvidenceText
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "with", "at", "by", "from", "is", "are", "was", "were", "be", "been", "it", "its",
        "this", "that", "these", "those", "as", "your", "you", "our", "we", "us", "their", "they", "do", "does", "did", "can", "could", "should", "will",
        "would", "what", "which", "who", "whom", "how", "why", "when", "where", "about", "than", "then", "so", "if", "not", "no", "yes", "any", "all", "each",
        "other", "into", "over", "also", "has", "have", "had", "i", "my", "me", "one", "more", "most", "such", "very", "per", "up", "out", "off", "etc",
    };

    public static List<TextToken> Tokens(string text)
    {
        var list = new List<TextToken>();
        foreach (Match m in WordPattern.Matches(text))
            list.Add(new TextToken(m.Value.ToLowerInvariant(), m.Index, m.Index + m.Length));
        return list;
    }

    public static bool IsStop(string word) => word.Length < 2 || Stop.Contains(word);

    /// <summary>A light stem (plural s), so "benefits" finds "benefit". Not linguistics - only enough to match the obvious.</summary>
    public static string Stem(string word) => word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss") ? word[..^1] : word;

    /// <summary>The words that carry meaning: no stop words, lowercased, lightly stemmed.</summary>
    public static List<string> Significant(string text) =>
        Tokens(text).Select(t => t.Word).Where(w => !IsStop(w) && w.Length >= 2).Select(Stem).ToList();
}

/// <summary>A passage the retriever may rank, with where it lives.</summary>
public sealed record PassageCandidate(Guid Id, Guid SourceId, string SourceName, int SourceVersion, string Location, string? Heading, string Text, string Hash);

public sealed record RankedPassage(PassageCandidate Passage, double Score);

/// <summary>
/// Finds the passages most relevant to a question by their words (BM25-style: rarer words count more, a short passage that hits beats a long
/// one that mentions it). A passage must share enough of the question's meaningful words to count at all - the relevance floor below which
/// the sources are taken NOT to answer the question, so nothing is drafted from a passage that merely sits nearby.
/// </summary>
public static class PassageRetriever
{
    private const double K1 = 1.4;
    private const double B = 0.7;

    /// <param name="hints">Extra words that mean the same thing as the question ("benefit, advantage, helps") - standard questions are generic.</param>
    public static List<RankedPassage> Rank(string question, string? hints, IReadOnlyList<PassageCandidate> passages, int take, double minCoverage = 0.34)
    {
        var queryTerms = EvidenceText.Significant(question).Distinct().ToList();
        var hintTerms = EvidenceText.Significant(hints ?? string.Empty).Distinct().ToList();
        if ((queryTerms.Count == 0 && hintTerms.Count == 0) || passages.Count == 0)
            return new List<RankedPassage>();

        var docs = passages.Select(p => (Passage: p, Terms: EvidenceText.Significant((p.Heading ?? string.Empty) + " " + p.Text))).ToList();
        var avgLen = Math.Max(1.0, docs.Average(d => d.Terms.Count));
        double Idf(string term)
        {
            var n = docs.Count(d => d.Terms.Contains(term));
            return Math.Log(1 + (docs.Count - n + 0.5) / (n + 0.5));
        }

        var all = queryTerms.Concat(hintTerms).Distinct().ToList();
        var idf = all.ToDictionary(t => t, Idf);
        var ranked = new List<RankedPassage>();

        foreach (var (passage, terms) in docs)
        {
            double score = 0;
            var matchedQuery = 0;
            foreach (var term in all)
            {
                var tf = terms.Count(t => t == term);
                if (tf == 0)
                    continue;
                if (queryTerms.Contains(term))
                    matchedQuery++;
                var weight = queryTerms.Contains(term) ? 1.0 : 0.6; // a question's own words count more than the hints
                score += weight * idf[term] * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * terms.Count / avgLen));
            }

            // The relevance floor: enough of the question's own words, or (for a generic question) enough of its hint words.
            var coverage = queryTerms.Count == 0 ? 0 : (double)matchedQuery / queryTerms.Count;
            var hintHits = hintTerms.Count(h => terms.Contains(h));
            // A generic question ("main benefit of your product") is carried by its hint words: two with a word of the question, three alone.
            var passes = coverage >= minCoverage || (hintTerms.Count > 0 && (matchedQuery >= 1 ? hintHits >= 2 : hintHits >= 3)) || (queryTerms.Count == 0 && hintHits >= 2);
            if (passes && score > 0)
                ranked.Add(new RankedPassage(passage, Math.Round(score, 4)));
        }

        return ranked.OrderByDescending(r => r.Score).Take(take).ToList();
    }
}

/// <summary>
/// Checks a quotation against a passage's real text. A citation is only ever kept when its words are really there - this is what stops an
/// invented quote from becoming "evidence". Case, spacing and punctuation do not matter; the words and their order do. The excerpt that is
/// kept is cut from the passage itself, never taken from the model's version of it.
/// </summary>
public static class QuoteVerifier
{
    public const int MinWords = 2;
    private const int FuzzyFromWords = 8;
    private const double FuzzyMatch = 0.9;

    /// <summary>The passage's own words matching <paramref name="quote"/>, or null when it is not in the passage.</summary>
    public static string? Locate(string passageText, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote))
            return null;

        var q = EvidenceText.Tokens(quote);
        if (q.Count < MinWords)
            return null;
        var p = EvidenceText.Tokens(passageText);
        if (p.Count < q.Count)
            return null;

        for (var i = 0; i + q.Count <= p.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < q.Count && ok; j++)
                ok = p[i + j].Word == q[j].Word;
            if (ok)
                return passageText[p[i].Start..p[i + q.Count - 1].End];
        }

        // A long quotation may differ by a word or two (a model smoothing a clause): accept a window that is nearly identical.
        if (q.Count >= FuzzyFromWords)
        {
            var best = (Index: -1, Hits: 0);
            for (var i = 0; i + q.Count <= p.Count; i++)
            {
                var hits = 0;
                var figuresMatch = true;
                for (var j = 0; j < q.Count; j++)
                {
                    if (p[i + j].Word == q[j].Word)
                        hits++;
                    else if (q[j].Word.Any(char.IsDigit) || p[i + j].Word.Any(char.IsDigit))
                        figuresMatch = false; // a changed number is a changed fact: never smoothed over
                }
                if (figuresMatch && hits > best.Hits)
                    best = (i, hits);
            }

            if (best.Index >= 0 && best.Hits >= Math.Ceiling(q.Count * FuzzyMatch))
                return passageText[p[best.Index].Start..p[best.Index + q.Count - 1].End];
        }

        return null;
    }
}

/// <summary>Figures (prices, percentages, quantities, durations) in text, and whether the evidence really contains them.</summary>
public static class ClaimRules
{
    private static readonly Regex Figure = new(
        @"(?<cur>₹|rs\.?|inr|\$|usd)?\s?(?<num>\d{1,3}(?:,\d{2,3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s?(?<unit>%|percent|x\b|times|days?|hours?|minutes?|weeks?|months?|years?|lakhs?|crores?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Material = new(
        @"\b(price|prices|pricing|cost|costs|fee|fees|charge|charges|rs\.?|inr|usd|₹|\$|guarantee|guaranteed|guarantees|warranty|certified|certification|certifications|iso\s?\d+|compliant|compliance|hipaa|gdpr|refund|refunds|money[- ]back|cure|cures|clinically|fda|approved by)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Outcome = new(
        @"(\d+(\.\d+)?\s?%|\d+(\.\d+)?\s?(x|times)\b|\b(double|triple|halve|halved)\b|\bsave[sd]?\s+(up to\s+)?[₹$]?\s?\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The figures worth checking: anything with a currency or unit, and any number of two or more digits. A lone small number ("3 steps") is not a claim.</summary>
    public static List<string> Figures(string text)
    {
        var found = new List<string>();
        foreach (Match m in Figure.Matches(text))
        {
            var digits = m.Groups["num"].Value.Replace(",", string.Empty);
            var significant = m.Groups["cur"].Success || m.Groups["unit"].Success || digits.TrimStart('0').Replace(".", string.Empty).Length >= 2;
            if (significant && !found.Contains(digits))
                found.Add(digits);
        }
        return found;
    }

    /// <summary>The figures in <paramref name="answer"/> that none of <paramref name="evidenceTexts"/> contains - claims nothing supports.</summary>
    public static List<string> Unsupported(string answer, IEnumerable<string> evidenceTexts)
    {
        var evidence = string.Join("\n", evidenceTexts);
        var normalised = Regex.Replace(evidence, @"(?<=\d),(?=\d)", string.Empty);
        return Figures(answer).Where(f => !Regex.IsMatch(normalised, $@"(?<![\d.]){Regex.Escape(f)}(?![\d])")).ToList();
    }

    /// <summary>A price, guarantee, certification, compliance, refund or outcome claim: it needs the tenant's explicit approval before customers hear it.</summary>
    public static bool IsMaterial(string text) => Material.IsMatch(text) || Outcome.IsMatch(text);

    /// <summary>A promised result as a number ("30% faster", "2x", "save 5000") - never accepted without evidence containing the same figure.</summary>
    public static bool HasQuantifiedOutcome(string text) => Outcome.IsMatch(text);
}

public sealed record EvidenceSnippet(Guid PassageId, string Excerpt, string PassageText);

public sealed record ConfidenceInput(
    string? Answer, IReadOnlyList<EvidenceSnippet> Evidence, bool ModelSaysInference, bool ModelSaysInsufficient, bool HasConflict, bool HasUnsupportedClaim,
    bool IsSimulated);

public sealed record ConfidenceResult(KnowledgeConfidence Confidence, KnowledgeAnswerKind Kind, IReadOnlyList<string> Reasons);

/// <summary>
/// The application's own assessment of how well the evidence supports an answer. It uses only what was verified - quotations found in real
/// passages, figures found in evidence, conflicts between sources - and never the model's confidence in itself, so a provider that always
/// sounds sure gets no more than a provider that hedges. The same rules apply to every provider and to the simulator.
///
///   NoEvidence - no verified quotation, or the sources were judged not to answer it.
///   Low        - a conflict between sources, a figure no evidence contains, or evidence that covers little of the answer.
///   Medium     - supported, but by an interpretation, by several passages combined, or covering only part of the answer.
///   High       - a single passage states it directly: not an inference, and the quotations cover most of what the answer says.
///
/// The simulator never reaches High, whatever it returns.
/// </summary>
public static class ConfidenceEngine
{
    public const double DirectCoverage = 0.6;
    public const double PartialCoverage = 0.3;
    public const int CombinedPassages = 3;

    public static ConfidenceResult Assess(ConfidenceInput input)
    {
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(input.Answer) || input.Evidence.Count == 0)
        {
            reasons.Add(input.Evidence.Count == 0 ? "No source passage could be quoted in support." : "There is no answer to assess.");
            return new ConfidenceResult(KnowledgeConfidence.NoEvidence, KnowledgeAnswerKind.AiInterpreted, reasons);
        }

        var quoted = string.Join(" ", input.Evidence.Select(e => e.Excerpt));
        var answerTerms = EvidenceText.Significant(input.Answer).Distinct().ToList();
        var evidenceTerms = EvidenceText.Significant(quoted).ToHashSet();
        var coverage = answerTerms.Count == 0 ? 0 : (double)answerTerms.Count(evidenceTerms.Contains) / answerTerms.Count;
        var distinctPassages = input.Evidence.Select(e => e.PassageId).Distinct().Count();
        var direct = !input.ModelSaysInference && coverage >= DirectCoverage && distinctPassages < CombinedPassages;

        var confidence = direct ? KnowledgeConfidence.High : coverage >= PartialCoverage ? KnowledgeConfidence.Medium : KnowledgeConfidence.Low;
        reasons.Add($"{input.Evidence.Count} verified quotation{(input.Evidence.Count == 1 ? string.Empty : "s")} from {distinctPassages} passage{(distinctPassages == 1 ? string.Empty : "s")}; they cover {coverage:P0} of the answer.");

        if (input.ModelSaysInference)
            reasons.Add("The answer interprets the sources rather than quoting them.");
        if (distinctPassages >= CombinedPassages)
            reasons.Add("The answer combines several passages.");

        if (input.HasUnsupportedClaim)
        {
            confidence = KnowledgeConfidence.Low;
            reasons.Add("The answer contains a figure that no source passage states.");
        }

        if (input.HasConflict)
        {
            confidence = KnowledgeConfidence.Low;
            reasons.Add("Sources disagree on a fact in this answer.");
        }

        if (input.ModelSaysInsufficient && confidence > KnowledgeConfidence.Low)
        {
            confidence = KnowledgeConfidence.Low;
            reasons.Add("The model itself said the sources are not enough.");
        }

        if (input.IsSimulated && confidence == KnowledgeConfidence.High)
        {
            confidence = KnowledgeConfidence.Medium;
            reasons.Add("The simulator produced this: it can never be High.");
        }

        var kind = confidence == KnowledgeConfidence.High ? KnowledgeAnswerKind.SourceExtracted : KnowledgeAnswerKind.AiInterpreted;
        return new ConfidenceResult(confidence, kind, reasons);
    }
}

public sealed record ConflictValue(string Value, string SourceName, string Location, string Sentence);

public sealed record DetectedConflict(string Fact, IReadOnlyList<ConflictValue> Values);

/// <summary>
/// Finds sources that disagree about a fact - a price, a duration, a percentage. Deliberately cautious: two sentences conflict only when
/// they are about the same thing (they share most of their meaningful words), say the same kind of fact, give different values, and come from
/// different passages. It would rather miss a conflict than report a false one, and it never decides which value is right - dates and
/// versions do not settle it; only the tenant does.
/// </summary>
public static class ConflictDetector
{
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?।\n])\s+", RegexOptions.Compiled);

    private static readonly (string Fact, Regex Kind)[] Kinds =
    {
        ("price", new Regex(@"(₹|rs\.?|inr|\$|usd)\s?\d|\b(price|cost|fee|charge|starts? at|starting at|only)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("duration", new Regex(@"\d+\s?(days?|weeks?|months?|years?|hours?|minutes?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("percentage", new Regex(@"\d+(\.\d+)?\s?(%|percent)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private const double SameSubject = 0.5;

    /// <summary>Words that make a sentence a price or a duration but say nothing about WHAT is priced: they must not make two different things look like the same thing.</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "rs", "inr", "usd", "cost", "price", "fee", "charge", "start", "starting", "only", "month", "week", "year", "day", "hour", "minute", "percent", "discount", "offer",
    };

    public static List<DetectedConflict> Detect(IReadOnlyList<PassageCandidate> passages)
    {
        var facts = new List<(string Fact, PassageCandidate Passage, string Sentence, HashSet<string> Subject, HashSet<string> Values)>();
        foreach (var passage in passages)
        {
            foreach (var sentence in SentenceSplit.Split(passage.Text).Select(s => s.Trim()).Where(s => s.Length >= 12))
            {
                var values = ClaimRules.Figures(sentence).ToHashSet();
                if (values.Count == 0)
                    continue;

                var subject = EvidenceText.Significant(Regex.Replace(sentence, @"[\d,.]+", " ")).Where(w => !GenericWords.Contains(w)).ToHashSet();
                foreach (var (fact, kind) in Kinds)
                {
                    if (kind.IsMatch(sentence))
                        facts.Add((fact, passage, sentence, subject, values));
                }
            }
        }

        var conflicts = new List<DetectedConflict>();
        var used = new HashSet<int>();
        for (var i = 0; i < facts.Count; i++)
        {
            for (var j = i + 1; j < facts.Count; j++)
            {
                var a = facts[i];
                var b = facts[j];
                if (a.Fact != b.Fact || a.Passage.Id == b.Passage.Id || a.Values.SetEquals(b.Values) || used.Contains(j))
                    continue;

                var shared = a.Subject.Intersect(b.Subject).Count();
                var smaller = Math.Min(a.Subject.Count, b.Subject.Count);
                if (smaller == 0 || (double)shared / smaller < SameSubject)
                    continue;

                // Overlapping value sets (a sentence quoting two prices, one of which matches) are not a disagreement.
                if (a.Values.Overlaps(b.Values))
                    continue;

                used.Add(j);
                conflicts.Add(new DetectedConflict(a.Fact, new[]
                {
                    new ConflictValue(string.Join(" / ", a.Values), a.Passage.SourceName, a.Passage.Location, a.Sentence),
                    new ConflictValue(string.Join(" / ", b.Values), b.Passage.SourceName, b.Passage.Location, b.Sentence),
                }));
            }
        }

        return conflicts;
    }
}

/// <summary>The twelve standard benefit and customer-value questions every tenant gets. They are a starting point drafted from sources, never a mandatory form.</summary>
public sealed record StandardQuestion(string Key, string Text, string Category, string Hints, string Clarifying);

public static class StandardQuestions
{
    public static readonly IReadOnlyList<StandardQuestion> All = new StandardQuestion[]
    {
        new("main-benefit", "What is the main benefit of your product?", "Benefits", "benefit advantage helps improve value main key",
            "In one sentence, what is the main thing your product does for a customer?"),
        new("customer-benefit", "How does your product benefit the customer?", "Benefits", "benefit customer helps improve easier faster save value",
            "How does a customer's day or business get better after they start using it?"),
        new("problem-solved", "What customer problem does your product solve?", "Problems", "problem challenge pain difficulty struggle solve issue need",
            "What problem or frustration do customers come to you with?"),
        new("why-choose", "Why should a customer choose your product over alternatives?", "Differentiators", "choose why better unlike compared alternative advantage reason",
            "What is the main reason a customer picks you over someone else?"),
        new("differentiators", "What makes your product different from competitors?", "Differentiators", "different unique only competitor unlike exclusive special",
            "What do you offer that competitors do not?"),
        new("ideal-customer", "Who is your ideal customer?", "Audience", "customer ideal target audience for suitable designed businesses patients clients users",
            "Who is your product best suited for?"),
        new("saves", "How does your product save customers time, money, or effort?", "Benefits", "save saves time money effort cost reduce faster automate cheaper",
            "How does it save a customer time, money or effort - in your own experience?"),
        new("expected-results", "What results or improvements can customers realistically expect?", "Benefits", "result outcome improvement expect achieve increase reduce typically",
            "What results do your customers usually see? Only what you can stand behind."),
        new("features-help", "How does each major feature help the customer?", "Features", "feature features includes offers provides capability helps allows",
            "Which are your main features, and what does each do for the customer?"),
        new("objections", "What concerns or objections do customers have before purchasing?", "Objections", "concern objection worry question hesitate doubt risk afraid cost trust",
            "What do customers usually worry about before buying, and what do you tell them?"),
        new("evidence", "What evidence supports the product's claimed benefits?", "Evidence", "evidence proof testimonial case study certified award reviews results rating trusted",
            "Do you have reviews, case studies, certificates or numbers that back up your claims?"),
        new("next-step", "What should a prospective customer do next?", "Next step", "contact call book start sign demo trial visit enquire next step",
            "What should an interested customer do to get started - call, book, visit?"),
    };
}
