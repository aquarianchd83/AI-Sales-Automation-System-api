using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The business's one next step (a demo, a visit...) is offered at most once, mid-conversation, never by a business that offers none,
/// and the language question comes before anything else.</summary>
public sealed class NextStepPromptTests
{
    private static AiConversationContext Context(ConversationGoal goal, string? detail = null, string? language = "en", params (MessageDirection Dir, string Text)[] history) => new(
        Guid.NewGuid(), "Asha", "What does it cost?",
        history.Select(h => new AiConversationTurn(h.Dir, h.Text, DateTime.UtcNow)).ToList(),
        Array.Empty<AiKnowledgeSnippet>(), null,
        new AiBusinessProfile("Clinic", null, null, null, null, null, null, goal, false, detail),
        Array.Empty<AiQualificationField>(), Array.Empty<AiCapturedField>(), Array.Empty<AiQualificationField>(), false, language, null, false);

    private static (MessageDirection, string) In(string text) => (MessageDirection.Inbound, text);
    private static (MessageDirection, string) Out(string text) => (MessageDirection.Outbound, text);

    [Fact]
    public void A_business_with_no_next_step_is_told_never_to_mention_one()
    {
        var context = Context(ConversationGoal.Enquiry);

        Assert.Contains("Never offer, suggest or mention one", AiPromptSupport.SystemPrompt(context));
        Assert.DoesNotContain("next step", AiPromptSupport.BuildUserMessage(context), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_system_prompt_carries_the_offer_and_only_the_tenants_own_description_of_it()
    {
        var prompt = AiPromptSupport.SystemPrompt(Context(ConversationGoal.Demo, "Free 30-minute demo on Zoom, Mon-Sat"));

        Assert.Contains("a demo", prompt);
        Assert.Contains("Free 30-minute demo on Zoom, Mon-Sat", prompt);
        Assert.Contains("at most ONCE", prompt);
    }

    [Fact]
    public void It_is_too_early_to_offer_it_in_the_first_exchanges()
    {
        var early = AiPromptSupport.BuildUserMessage(Context(ConversationGoal.Demo, null, "en", In("hi"), Out("Hello!")));

        Assert.Contains("too early to offer the next step", early);
    }

    [Fact]
    public void After_a_couple_of_questions_it_may_be_offered_once()
    {
        var history = new[] { In("what is it"), Out("It manages OPD."), In("does it do invoices") , Out("Yes, it edits invoices.") };
        var prompt = AiPromptSupport.BuildUserMessage(Context(ConversationGoal.Demo, null, "en", history));

        Assert.Contains("You may now mention the next step ONCE", prompt);
    }

    [Fact]
    public void Once_offered_it_is_not_offered_again()
    {
        var history = new[] { In("what is it"), Out("It manages OPD."), In("and invoices?"), Out("Yes. We also offer a free demo if you'd like."), In("ok, what about price") };
        var prompt = AiPromptSupport.BuildUserMessage(Context(ConversationGoal.Demo, null, "en", history));

        Assert.Contains("already offered", prompt);
        Assert.DoesNotContain("You may now mention", prompt);
    }

    [Fact]
    public void A_first_message_with_no_chosen_language_gets_the_language_question_only()
    {
        var prompt = AiPromptSupport.BuildUserMessage(Context(ConversationGoal.Enquiry, null, language: null));

        Assert.Contains("Language not chosen yet, and this is the customer's first message", prompt);
        Assert.Contains("Do NOT answer it yet", prompt);
    }

    [Fact]
    public void A_chosen_language_is_the_only_one_replied_in()
    {
        var prompt = AiPromptSupport.BuildUserMessage(Context(ConversationGoal.Enquiry, null, language: "hi-Latn"));

        Assert.Contains("Chosen language: \"hi-Latn\"", prompt);
        Assert.Contains("ONLY in this language", prompt);
    }

    [Fact]
    public void An_answer_like_all_topics_is_an_answer_and_the_confirmation_does_not_end_with_a_question()
    {
        var prompt = AiPromptSupport.SystemPrompt(Context(ConversationGoal.Demo, "Free 30-minute demo"));

        Assert.Contains("\"All of them\", \"everything\", \"anything\", \"no preference\" and \"you decide\" ARE answers", prompt);
        Assert.Contains("never ask that question again", prompt);
        Assert.Contains("must NOT end with a", prompt);
        Assert.Contains("must not repeat any question already asked", prompt);
        // It asks only for what arranging the step needs, one thing at a time, never what it was already told.
        Assert.Contains("one thing at a time, and never for something they have already told you", prompt);
    }

    [Fact]
    public void The_ai_asks_for_an_email_for_the_confirmation_and_never_offers_an_invite_or_meeting_link()
    {
        var prompt = AiPromptSupport.SystemPrompt(Context(ConversationGoal.Demo, "Free 30-minute demo"));

        Assert.Contains("the email address a confirmation should be sent to", prompt);
        Assert.Contains("never say or imply that an email will be sent", prompt);
        Assert.Contains("customer_email", prompt);
        Assert.Contains("WITHOUT customer_email no email is sent", prompt);
        Assert.Contains("You cannot send calendar invitations or meeting links", prompt);
        Assert.DoesNotContain("next_step_starts_at", prompt);
    }

    [Fact]
    public void The_ai_is_told_when_to_report_a_booking_and_that_it_has_no_calendar()
    {
        var prompt = AiPromptSupport.SystemPrompt(Context(ConversationGoal.Demo));

        Assert.Contains("next_step_booked", prompt);
        Assert.Contains("next_step_summary", prompt);
        Assert.Contains("You have no calendar", prompt);
        Assert.Contains("not again for the same booking", prompt);
    }

    [Fact]
    public void The_booking_report_is_part_of_what_the_ai_can_answer_with_but_never_required()
    {
        var schema = System.Text.Json.JsonSerializer.Serialize(AiPromptSupport.ToolInputSchema(Array.Empty<AiQualificationField>()));

        Assert.Contains("\"next_step_booked\"", schema);
        Assert.Contains("\"next_step_summary\"", schema);
        // Required lists only the always-present fields, so a reply that is not a booking omits both.
        var required = schema[schema.LastIndexOf("\"required\"", StringComparison.Ordinal)..];
        Assert.DoesNotContain("next_step_booked", required);
    }

    [Fact]
    public void A_reply_that_reports_a_booking_carries_it_through_and_one_that_does_not_reports_none()
    {
        var booked = System.Text.Json.JsonSerializer.Deserialize<ToolResultPayload>(
            """{"intent":"Interested","confidence":0.9,"response_text":"Scheduled.","next_step_booked":true,"next_step_summary":"  Demo Mon 2 Nov 4 PM IST  "}""")!;
        var plain = System.Text.Json.JsonSerializer.Deserialize<ToolResultPayload>("""{"intent":"Interested","confidence":0.9,"response_text":"Sure."}""")!;

        var result = booked.ToAiReplyResult("Test:fake", 10, 5, TimeSpan.FromMilliseconds(1), null);
        var none = plain.ToAiReplyResult("Test:fake", 10, 5, TimeSpan.FromMilliseconds(1), null);

        Assert.True(result.NextStepBooked);
        Assert.Equal("Demo Mon 2 Nov 4 PM IST", result.NextStepSummary);
        Assert.False(none.NextStepBooked);
        Assert.Null(none.NextStepSummary);
    }
}
