using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Common;

/// <summary>
/// Decides, from the text of an inbound message alone, whether the customer asked us to stop.
///
/// Covers the two deterministic layers of the three the design describes. Layer 1 is the original
/// exact-match keyword list. Layer 2 is a phrase-pattern pass over the same text: "don't message me",
/// "remove me", "no more messages", "message mat bhejo" - all of which are in the agent's own
/// published opt-out list and none of which the keyword list catches, because it compares the whole
/// trimmed message against seven fixed words. Layer 3 is the model, and lives in the orchestrator;
/// this runs in the webhook processor before any AI call, so a plain STOP still costs nothing.
///
/// <para>
/// The bias here is deliberate and asymmetric. A false positive means we stop sending promotional
/// messages to someone who may not have asked. A false negative means we keep sending to someone who
/// did - which is a compliance violation. Those are not equal, so this leans toward detecting.
/// </para>
///
/// <para>
/// What it still will not catch: Devanagari and other non-Latin scripts, and any phrasing nobody
/// anticipated. Those are Layer 3's job, and the reason Layer 3 exists at all.
/// </para>
/// </summary>
public static class OptOutDetector
{
    /// <summary>Case-insensitive, exact-match (after trim) - deliberately not a substring match, so
    /// "please stop calling me about the noise" is not mistaken for an opt-out by the word "stop"
    /// alone. The phrase patterns below are what handle the cases this cannot.</summary>
    private static readonly HashSet<string> ExactKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "unsubscribe", "unsub", "cancel", "opt out", "optout", "quit"
    };

    /// <summary>
    /// Beyond this, the text is not a request to stop - it is a conversation that happens to contain
    /// one of these words. Someone asking us to stop says so early; a 2,000-character message that
    /// mentions "remove me" in passing is exactly the false positive worth avoiding, and Layer 3 sees
    /// the whole message anyway.
    /// </summary>
    private const int MaxInspectedChars = 400;

    /// <summary>
    /// Bounded as cheap insurance rather than because any pattern below can backtrack badly - none
    /// nest a quantifier inside another. A timeout is treated as "no match" (see
    /// <see cref="Detect"/>), because Layer 3 still sees this message.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private const RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// English, Hindi-in-Latin-script (Hinglish) and the mixtures people actually send.
    ///
    /// <para>Every pattern is anchored on either "me" or a messaging noun, because the bare verb is
    /// almost always ambiguous inside a sales conversation. "stop messaging me" is an opt-out;
    /// "stop, the 3BHK is the one I meant" is not. "mujhe message nahi chahiye" is an opt-out;
    /// "3BHK nahi chahiye" is a customer narrowing their requirement, and opting them out over it
    /// would be worse than useless. Leaning toward detection does not mean matching loose verbs.</para>
    ///
    /// <para>"don't call me" is the one deliberate exception to needing a messaging noun: as a whole
    /// request it reads as "do not contact me", not as a complaint about one channel.</para>
    /// </summary>
    private static readonly Regex[] PhrasePatterns =
    {
        // don't message me · do not text me · dont contact me · don't call me
        new(@"\b(don'?t|do not|dont)\s+(message|msg|text|contact|call)\s+me\b", Options, MatchTimeout),

        // remove me · delete my number · take me off your list
        new(@"\b(remove|delete)\s+(me|my\s+(number|contact|details))\b", Options, MatchTimeout),
        new(@"\btake\s+me\s+off\b", Options, MatchTimeout),

        // no more messages · no more updates
        new(@"\bno\s+more\s+(messages?|msgs?|updates?|texts?|sms)\b", Options, MatchTimeout),

        // I don't want this · don't want any more messages
        new(@"\b(don'?t|do not|dont)\s+want\s+(this|these)\b", Options, MatchTimeout),
        new(@"\b(don'?t|do not|dont)\s+want\s+.{0,15}\b(messages?|msgs?|sms|updates?|texts?)\b", Options, MatchTimeout),

        // stop messaging me · stop these messages · band karo ye messages
        new(@"\b(stop|band\s*kar\w*)\b.{0,15}\b(message|msg|sms|text|update)", Options, MatchTimeout),

        // unsubscribe me · how do I unsubscribe
        new(@"\bunsub(scribe)?\b", Options, MatchTimeout),

        // message mat bhejo · mujhe mat bhejo
        new(@"\bmat\s+bhej\w*\b", Options, MatchTimeout),

        // mujhe message nahi chahiye · koi update nahi chahiye
        new(@"\b(messages?|msgs?|sms|updates?|call)\w*\b.{0,20}\bnahi\s+chahiye\b", Options, MatchTimeout)
    };

    /// <summary>
    /// The detection layer that matched, or null if neither did. Never returns
    /// <see cref="OptOutSource.AiDetected"/> or <see cref="OptOutSource.Manual"/> - those are set by
    /// the orchestrator and by a human respectively, and are separate values precisely so a compliance
    /// review can tell which of the four produced a given opt-out.
    /// </summary>
    public static OptOutSource? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();

        if (ExactKeywords.Contains(trimmed))
            return OptOutSource.ExactKeyword;

        var inspected = trimmed.Length <= MaxInspectedChars ? trimmed : trimmed[..MaxInspectedChars];

        try
        {
            foreach (var pattern in PhrasePatterns)
            {
                if (pattern.IsMatch(inspected))
                    return OptOutSource.PhrasePattern;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Not silently losing the opt-out: the message still reaches the orchestrator, and the
            // model's own opt-out flag (Layer 3) is checked before any reply is sent.
            return null;
        }

        return null;
    }
}
