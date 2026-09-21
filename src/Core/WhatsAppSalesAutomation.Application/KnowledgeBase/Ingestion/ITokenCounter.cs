using System.Globalization;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>
/// How many tokens a piece of text costs.
///
/// Behind an interface because the right answer is provider-specific - OpenAI's <c>cl100k_base</c>
/// and Anthropic's tokenizer disagree, and both disagree with any estimate. Chunk sizing, the
/// context-window budget and the cost model all read this, so a wrong count is wrong in three places
/// at once.
/// </summary>
public interface ITokenCounter
{
    int Count(string? text);
}

/// <summary>
/// A script-aware estimate, used until a real tokenizer is wired in.
///
/// The Phase 6 document is explicit that <c>chars / 4</c> is not acceptable, and it is right about
/// why: that ratio is calibrated on English, and Devanagari costs far more tokens per character
/// under every byte-pair vocabulary in use - the document puts the error at up to 2x. Since Phase 6
/// carries Hindi content, a single global ratio would undersize every Hindi chunk, which shows up as
/// chunks that overflow the model's real budget rather than as an obviously wrong number.
///
/// So this counts by script instead: Latin text at roughly four characters per token, Devanagari and
/// other non-Latin scripts at roughly one and a half, and CJK at one. It is still an estimate, and
/// it is deliberately biased to OVER-count rather than under-count - an over-estimate yields a
/// slightly small chunk, while an under-estimate yields one that does not fit.
///
/// Replacing this with a real tokenizer is a matter of another <see cref="ITokenCounter"/>; nothing
/// else changes.
/// </summary>
public sealed class HeuristicTokenCounter : ITokenCounter
{
    /// <summary>Latin script and digits. The familiar ~4 chars/token.</summary>
    private const double LatinCharsPerToken = 4.0;

    /// <summary>Devanagari and other non-Latin alphabets. Byte-pair vocabularies trained mostly on
    /// English split these far more finely - often close to one token per character once combining
    /// marks are counted.</summary>
    private const double IndicCharsPerToken = 1.5;

    /// <summary>CJK ideographs are approximately one token each.</summary>
    private const double CjkCharsPerToken = 1.0;

    public int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        double latin = 0, indic = 0, cjk = 0;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                // Whitespace is usually absorbed into an adjacent token rather than costing its own,
                // so it is counted with Latin rather than dropped - dropping it would undercount a
                // heavily formatted document such as a table.
                latin++;
                continue;
            }

            if (IsCjk(ch))
                cjk++;
            else if (IsLatinOrCommon(ch))
                latin++;
            else
                indic++;
        }

        var estimate = latin / LatinCharsPerToken
                     + indic / IndicCharsPerToken
                     + cjk / CjkCharsPerToken;

        // Never report zero for non-empty text: a zero-cost block would pack into a chunk without
        // limit, which is the one estimation error that produces an unbounded chunk.
        return Math.Max(1, (int)Math.Ceiling(estimate));
    }

    private static bool IsLatinOrCommon(char ch) =>
        ch < 0x0250 || char.GetUnicodeCategory(ch) is UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol;

    private static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF)      // CJK Unified Ideographs
        || (ch >= 0x3040 && ch <= 0x30FF)   // Hiragana + Katakana
        || (ch >= 0xAC00 && ch <= 0xD7AF);  // Hangul syllables
}
