using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The checks that stand between the model and the customer.
///
/// Two of these matter more than the rest. <see cref="Invented_numbers_block_the_reply"/> catches the
/// most expensive mistake a sales agent can make - a price or date that came from nowhere - and
/// <see cref="Internal_terms_block_the_reply"/> catches the agent describing its own machinery to
/// someone who should only ever see a salesperson.
/// </summary>
public class AiReplyValidatorTests
{
    private static readonly Guid ChunkId = Guid.NewGuid();
    private readonly AiReplyValidator _validator = new();

    // ── Blocking checks ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_clean_reply_passes()
    {
        var result = _validator.Validate(Reply("Our 3BHK units start at 95 lakh. Shall I share the floor plan?"), Context());

        Assert.True(result.CanSend);
        Assert.False(result.HasFailures);
    }

    [Fact]
    public void Empty_reply_blocks()
    {
        var result = _validator.Validate(Reply("   "), Context());

        Assert.False(result.CanSend);
        Assert.Contains(result.Failures, f => f.Code == "EmptyResponse");
    }

    [Fact]
    public void Overlong_reply_blocks()
    {
        var result = _validator.Validate(Reply(new string('a', AiReplyValidator.MaxResponseChars + 1)), Context());

        Assert.False(result.CanSend);
        Assert.Contains(result.Failures, f => f.Code == "ResponseTooLong");
    }

    [Theory]
    [InlineData("Let me check our knowledge base for that.")]
    [InlineData("My system prompt does not cover this.")]
    [InlineData("As an AI, I cannot confirm that.")]
    [InlineData("Based on the retrieval results, the price is 95 lakh.")]
    public void Internal_terms_block_the_reply(string text)
    {
        var result = _validator.Validate(Reply(text), Context());

        Assert.False(result.CanSend);
        Assert.Contains(result.Failures, f => f.Code == "InternalTermLeak");
    }

    [Fact]
    public void Internal_term_check_respects_word_boundaries()
    {
        // "promptly" contains "prompt". A substring match here would block ordinary sales language.
        var result = _validator.Validate(Reply("We will get back to you promptly."), Context());

        Assert.True(result.CanSend);
    }

    [Fact]
    public void Score_talk_blocks_unless_the_tenant_allows_it()
    {
        var blocked = _validator.Validate(Reply("You are a hot lead for us!"), Context());
        Assert.False(blocked.CanSend);
        Assert.Contains(blocked.Failures, f => f.Code == "ScoreDisclosure");

        var allowed = _validator.Validate(Reply("You are a hot lead for us!"), Context(mayDiscloseScore: true));
        Assert.True(allowed.CanSend);
    }

    // ── Numeric grounding ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Invented_numbers_block_the_reply()
    {
        // Nothing in the knowledge, the history or the customer's message mentions 47 lakh.
        var result = _validator.Validate(Reply("That unit is available at 4700000."), Context());

        Assert.False(result.CanSend);
        Assert.Contains(result.Failures, f => f.Code == "UngroundedNumber");
    }

    [Fact]
    public void A_number_from_the_knowledge_is_fine()
    {
        var result = _validator.Validate(Reply("They start at 9500000."), Context());

        Assert.True(result.CanSend);
    }

    [Fact]
    public void A_number_the_customer_supplied_is_fine()
    {
        var context = Context(inbound: "my budget is 8000000");

        var result = _validator.Validate(Reply("8000000 works - I can show you two options."), context);

        Assert.True(result.CanSend);
    }

    [Fact]
    public void Thousands_separators_do_not_make_a_grounded_number_look_invented()
    {
        // The knowledge says 9500000; the reply writes it the way an Indian customer reads it.
        var result = _validator.Validate(Reply("They start at 95,00,000."), Context());

        Assert.True(result.CanSend);
    }

    [Fact]
    public void Small_numbers_are_left_alone()
    {
        // Step numbers and counts are prose, not claims - flagging them would block almost every reply.
        var result = _validator.Validate(Reply("There are 3 options, and 2 of them are ready to move in."), Context());

        Assert.True(result.CanSend);
    }

    // ── Non-blocking checks: the reply survives, the bad part does not ────────────────────────

    [Fact]
    public void A_field_outside_the_schema_is_dropped_without_blocking_the_reply()
    {
        var reply = Reply("Noted, thank you.") with
        {
            ExtractedFields = new[]
            {
                new AiExtractedField("budget", "50 lakh", 0.9),
                new AiExtractedField("favourite_colour", "blue", 0.9)
            }
        };

        var result = _validator.Validate(reply, Context());

        Assert.True(result.CanSend);
        Assert.Single(result.ExtractedFields);
        Assert.Equal("budget", result.ExtractedFields[0].FieldKey);
        Assert.Contains(result.Failures, f => f.Code == "UnknownField" && !f.Blocking);
    }

    [Fact]
    public void An_intent_outside_the_enum_becomes_Unknown()
    {
        var reply = Reply("Noted.") with { DetectedIntent = "FeelingChatty" };

        var result = _validator.Validate(reply, Context());

        Assert.Equal(nameof(CustomerIntent.Unknown), result.DetectedIntent);
        Assert.Contains(result.Failures, f => f.Code == "UnknownIntent" && !f.Blocking);
        Assert.True(result.CanSend);
    }

    [Fact]
    public void A_citation_this_turn_was_not_given_is_dropped()
    {
        var reply = Reply("They start at 9500000.") with { CitedChunkIds = new[] { ChunkId, Guid.NewGuid() } };

        var result = _validator.Validate(reply, Context());

        Assert.Single(result.CitedChunkIds);
        Assert.Equal(ChunkId, result.CitedChunkIds[0]);
        Assert.Contains(result.Failures, f => f.Code == "HallucinatedCitation" && !f.Blocking);
    }

    [Fact]
    public void Claiming_to_have_asked_an_unoffered_field_is_recorded_but_not_trusted()
    {
        var reply = Reply("Noted.") with { AskedFieldKey = "interest" };   // only 'budget' was offered

        var result = _validator.Validate(reply, Context());

        Assert.Null(result.AskedFieldKey);
        Assert.Contains(result.Failures, f => f.Code == "UnofferedField" && !f.Blocking);
        Assert.True(result.CanSend);
    }

    [Fact]
    public void Agent_note_is_truncated_rather_than_stored_whole()
    {
        var reply = Reply("Noted.") with { AgentNote = new string('x', 900) };

        var result = _validator.Validate(reply, Context());

        Assert.Equal(500, result.AgentNote!.Length);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static AiReplyResult Reply(string text) => new(
        ResponseText: text,
        DetectedIntent: nameof(CustomerIntent.PriceEnquiry),
        ConfidenceScore: 0.9,
        ExtractedEntities: new AiExtractedEntities(null, null, null),
        UpdatedSummary: "Customer asked about price.",
        ModelUsed: "Test:fake",
        PromptTokens: 100,
        CompletionTokens: 20,
        LatencyMs: 10,
        CitedChunkIds: new[] { ChunkId });

    private static AiConversationContext Context(
        bool mayDiscloseScore = false,
        string inbound = "what is the price?") => new(
        ConversationId: Guid.NewGuid(),
        CustomerName: "Raj",
        InboundMessageText: inbound,
        RecentHistory: Array.Empty<AiConversationTurn>(),
        GroundingChunks: new[]
        {
            new AiKnowledgeSnippet(ChunkId, "3BHK units in Sector 82 start at 9500000.", 0.9)
        },
        ExistingSummary: null,
        Business: new AiBusinessProfile(
            "Acme Realty", "Real estate", "Mohali", null, null, null, null,
            ConversationGoal.SiteVisit, mayDiscloseScore),
        SchemaFields: new[]
        {
            new AiQualificationField("budget", "Budget", null, "What is your budget?", QualificationDataType.Currency, null),
            new AiQualificationField("interest", "Interest", null, "What are you looking for?", QualificationDataType.Text, null)
        },
        KnownFields: Array.Empty<AiCapturedField>(),
        FieldsToAsk: new[]
        {
            new AiQualificationField("budget", "Budget", null, "What is your budget?", QualificationDataType.Currency, null)
        },
        QualificationPaused: false,
        PreferredLanguage: null);
}
