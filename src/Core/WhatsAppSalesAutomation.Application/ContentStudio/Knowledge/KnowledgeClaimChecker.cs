using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;

/// <summary>
/// Decides whether each sentence a piece of content wants to say is backed by something the tenant has verified. Deliberately blunt and
/// deterministic - no AI in the loop, so the answer is the same every time and can be explained: a claim is supported when most of its
/// meaningful words appear in one verified, current fact and every number it quotes appears in that fact too. A claim that sounds like
/// a result or a guarantee (percentages, "guaranteed", "best", "painless", "cure") must also be backed by a fact the tenant has proof
/// for. Everything else is flagged rather than quietly allowed through.
/// </summary>
public static class KnowledgeClaimChecker
{
    private const double SupportThreshold = 0.6;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "for", "with", "is", "are", "was", "were", "be", "been", "we", "our", "us", "you", "your",
        "it", "its", "this", "that", "these", "those", "by", "as", "from", "can", "will", "has", "have", "had", "do", "does", "not", "all", "any", "more", "than"
    };

    private static readonly Regex Word = new("[a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);

    private static readonly Regex Risky = new(
        @"\d\s*%|\bpercent\b|\bguarantee[ds]?\b|\b100\s*%|\bbest\b|\bno\.?\s*1\b|\bnumber one\b|\bcure[ds]?\b|\bpermanent(ly)?\b|\bpainless\b|\brisk[- ]free\b|\bproven\b|\bclinically\b|\bnever fail|\balways works?\b|\bsuccess rate\b|\bresults?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ClaimCheckResultDto Check(IEnumerable<string> claims, IReadOnlyList<BusinessFact> facts, DateTime utcNow)
    {
        var live = facts.Where(f => f.Status != BusinessFactStatus.Retired).ToList();
        var usable = live.Where(f => KnowledgeClassifier.IsUsable(f, utcNow)).Select(Prepare).ToList();
        var unverified = live.Where(f => !KnowledgeClassifier.IsUsable(f, utcNow)).Select(Prepare).ToList();

        var results = new List<ClaimCheckDto>();
        foreach (var claim in claims.Where(c => !string.IsNullOrWhiteSpace(c)))
            results.Add(CheckOne(claim.Trim(), usable, unverified));

        return new ClaimCheckResultDto(results, results.Count(r => r.Verdict == ClaimVerdict.Supported), results.Count(r => r.Verdict != ClaimVerdict.Supported));
    }

    private static ClaimCheckDto CheckOne(string claim, IReadOnlyList<Prepared> usable, IReadOnlyList<Prepared> unverified)
    {
        var tokens = Tokens(claim);
        var numbers = Numbers(claim);
        var risky = Risky.IsMatch(claim);

        if (tokens.Count == 0)
            return new ClaimCheckDto(claim, ClaimVerdict.Unsupported, "There is nothing here to check against your facts.", Array.Empty<Guid>(), risky);

        var supporting = usable.Where(f => Backs(f, tokens, numbers)).ToList();
        if (supporting.Count > 0)
        {
            if (risky && supporting.All(f => string.IsNullOrWhiteSpace(f.Fact.Evidence)))
            {
                return new ClaimCheckDto(claim, ClaimVerdict.Unverified,
                    "A verified fact covers this, but it sounds like a result or a guarantee and there is no proof recorded for it. Add how you know it is true.",
                    supporting.Select(f => f.Fact.Id).ToList(), true);
            }

            return new ClaimCheckDto(claim, ClaimVerdict.Supported, "Matches a fact you have verified.", supporting.Select(f => f.Fact.Id).ToList(), risky);
        }

        var heard = unverified.Where(f => Backs(f, tokens, numbers)).ToList();
        if (heard.Count > 0)
        {
            return new ClaimCheckDto(claim, ClaimVerdict.Unverified,
                "You have told us something like this, but nobody has verified it yet (or it has expired). It cannot be stated until it is confirmed.",
                heard.Select(f => f.Fact.Id).ToList(), risky);
        }

        var mismatch = numbers.Count > 0 && usable.Any(f => Overlap(tokens, f.Tokens) >= SupportThreshold);
        return new ClaimCheckDto(claim, ClaimVerdict.Unsupported,
            mismatch
                ? "A verified fact covers this topic, but not these numbers. A figure that is not in your facts must not be used."
                : "None of your facts supports this. Add and verify a fact for it, or take it out.",
            Array.Empty<Guid>(), risky);
    }

    private static bool Backs(Prepared fact, IReadOnlySet<string> claimTokens, IReadOnlySet<string> claimNumbers) =>
        Overlap(claimTokens, fact.Tokens) >= SupportThreshold && claimNumbers.IsSubsetOf(fact.Numbers);

    private static double Overlap(IReadOnlySet<string> claim, IReadOnlySet<string> fact) =>
        claim.Count == 0 ? 0 : (double)claim.Count(fact.Contains) / claim.Count;

    private sealed record Prepared(BusinessFact Fact, HashSet<string> Tokens, HashSet<string> Numbers);

    private static Prepared Prepare(BusinessFact fact)
    {
        var text = string.Join(' ', new[] { fact.Category, fact.Subject, fact.Statement, fact.Context }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new Prepared(fact, Tokens(text), Numbers(text));
    }

    private static HashSet<string> Tokens(string text) =>
        Word.Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w.Length > 1 && !StopWords.Contains(w) && !w.All(char.IsDigit))
            .Select(Stem)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Numbers(string text) =>
        Number.Matches(text).Select(m => m.Value.Replace(",", string.Empty)).ToHashSet(StringComparer.Ordinal);

    private static string Stem(string word) =>
        word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal) ? word[..^3] + "y"
        : word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1]
        : word;
}
