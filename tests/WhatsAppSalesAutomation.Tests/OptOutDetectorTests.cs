using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Pins both deterministic opt-out layers, in both directions.
///
/// The catches matter because missing one is a compliance violation. The non-catches matter just as
/// much, and are easier to get wrong: this runs inside a sales conversation where customers say "not
/// this one", "3BHK nahi chahiye" and "stop, I meant the other flat" all day, and a pattern loose
/// enough to read those as opt-outs would silently end the tenant's ability to reach a live lead.
/// </summary>
public class OptOutDetectorTests
{
    // ── Layer 1: exact keywords ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("stop")]
    [InlineData("STOP")]
    [InlineData("  Stop  ")]
    [InlineData("unsubscribe")]
    [InlineData("unsub")]
    [InlineData("cancel")]
    [InlineData("opt out")]
    [InlineData("optout")]
    [InlineData("quit")]
    public void Exact_keywords_are_reported_as_exact(string text)
    {
        Assert.Equal(OptOutSource.ExactKeyword, OptOutDetector.Detect(text));
    }

    // ── Layer 2: the phrases the keyword list never caught ──────────────────────────────────

    [Theory]
    // Every row of the design doc's own table that the old exact-match list missed.
    [InlineData("Don't message me")]
    [InlineData("dont message me again")]
    [InlineData("do not contact me")]
    [InlineData("Please don't call me")]
    [InlineData("Remove me")]
    [InlineData("please remove me from your list")]
    [InlineData("delete my number")]
    [InlineData("take me off your list")]
    [InlineData("No more messages")]
    [InlineData("no more updates please")]
    [InlineData("I don't want this")]
    [InlineData("I dont want any more messages")]
    [InlineData("stop sending me messages")]
    [InlineData("band karo ye messages")]
    [InlineData("mujhe message mat bhejo")]
    [InlineData("mujhe koi message nahi chahiye")]
    [InlineData("how do I unsubscribe")]
    public void Phrases_are_reported_as_pattern(string text)
    {
        Assert.Equal(OptOutSource.PhrasePattern, OptOutDetector.Detect(text));
    }

    // ── The other direction: a sales conversation is not an opt-out ─────────────────────────

    [Theory]
    // The original exact-match comment's own example - "stop" inside a sentence about something else.
    [InlineData("please stop calling me about the noise complaint")]
    // A customer narrowing their requirement, which is the opposite of leaving.
    [InlineData("3BHK nahi chahiye, 2BHK dikhao")]
    [InlineData("ye wala nahi chahiye")]
    [InlineData("I don't want the ground floor one")]
    [InlineData("not interested in that one, show me the corner flat")]
    // Ordinary conversation containing a bare verb the patterns deliberately do not match alone.
    [InlineData("abhi mat karo, kal baat karte hain")]
    [InlineData("can you stop by the site tomorrow?")]
    [InlineData("please send me the brochure")]
    [InlineData("call me at 5pm")]
    public void Ordinary_conversation_is_not_an_opt_out(string text)
    {
        Assert.Null(OptOutDetector.Detect(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_read_is_not_an_opt_out(string? text)
    {
        Assert.Null(OptOutDetector.Detect(text));
    }

    // ── Bounds ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_keyword_inside_a_sentence_is_not_an_exact_match()
    {
        // Layer 1 compares the whole trimmed message; "cancel my viewing" is a request about an
        // appointment, not about marketing consent, and no phrase pattern claims it either.
        Assert.Null(OptOutDetector.Detect("cancel my viewing on Saturday"));
    }

    [Fact]
    public void A_phrase_buried_far_into_a_long_message_is_left_to_the_model()
    {
        // Someone asking us to stop says so early. Past the inspection window this is a conversation
        // that happens to contain the words, and Layer 3 still reads the whole thing.
        var text = new string('x', 500) + " remove me";

        Assert.Null(OptOutDetector.Detect(text));
    }

    [Fact]
    public void A_phrase_inside_the_window_is_still_caught()
    {
        var text = "hi, I had a question about the Mohali project. " + new string('x', 200) + " remove me from your list";

        Assert.Equal(OptOutSource.PhrasePattern, OptOutDetector.Detect(text));
    }

    [Fact]
    public void Exact_match_wins_over_pattern_so_the_audit_trail_names_the_cheaper_layer()
    {
        // "unsubscribe" satisfies both layers. Reporting ExactKeyword is not cosmetic: it records
        // that no pattern judgement was involved, which is what a compliance review is asking.
        Assert.Equal(OptOutSource.ExactKeyword, OptOutDetector.Detect("unsubscribe"));
        Assert.Equal(OptOutSource.PhrasePattern, OptOutDetector.Detect("please unsubscribe me"));
    }
}
