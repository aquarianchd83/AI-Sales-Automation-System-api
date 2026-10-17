using System.Text.Json;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Phase 1 of the Business Q&amp;A wizard: "I don't know" / "I need help" / "Not applicable", the refusal of secrets, and the new
/// definition fields (example, group, require-known, fact subject/context/validity).</summary>
public sealed class QuestionnaireSpecialAnswerTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Answers(params (string Key, string Json)[] answers) =>
        answers.ToDictionary(a => a.Key, a => J(a.Json), StringComparer.Ordinal);

    private const string Unknown = """{ "$special": "unknown" }""";

    private const string Definition = """
    {
      "questions": [
        { "key": "name", "type": "ShortText", "prompt": "Name?", "required": true, "requireKnown": true, "group": "About", "example": "Sunrise Clinic",
          "fact": { "category": "About", "template": "Called {value}." } },
        { "key": "price", "type": "ShortText", "prompt": "Price?", "required": true, "group": "Money", "fact": { "category": "Pricing", "template": "Costs {value}.", "validDays": 180 } },
        { "key": "has_branch", "type": "Boolean", "prompt": "Branch?" },
        { "key": "branch_city", "type": "ShortText", "prompt": "Which city?", "required": true,
          "showWhen": { "questionKey": "has_branch", "operator": "Equal", "value": true } },
        { "key": "hours", "type": "Structured", "prompt": "Hours?", "required": true,
          "fields": [ { "key": "weekdays", "label": "Mon-Fri", "required": true } ] },
        { "key": "after_branch", "type": "ShortText", "prompt": "Anything else?",
          "showWhen": { "questionKey": "has_branch", "operator": "Answered" } }
      ]
    }
    """;

    private static QuestionnaireDefinition Parsed() => QuestionnaireEngine.Parse(Definition);

    // ----- special answers -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("unknown")]
    [InlineData("need_help")]
    [InlineData("not_applicable")]
    public void Each_special_answer_is_accepted_for_a_question_that_does_not_require_a_real_answer(string kind)
    {
        var issues = QuestionnaireEngine.ValidateForSave(Parsed(), Answers(("price", $$"""{ "$special": "{{kind}}" }""")));
        Assert.Empty(issues);
    }

    [Fact]
    public void A_special_answer_is_refused_for_a_question_that_requires_a_real_answer()
    {
        var issues = QuestionnaireEngine.ValidateForSave(Parsed(), Answers(("name", Unknown)));
        Assert.Contains(issues, i => i.Key == "name" && i.Message.Contains("real answer"));
    }

    [Theory]
    [InlineData("""{ "$special": "maybe" }""")]
    [InlineData("""{ "$special": 3 }""")]
    [InlineData("""{ "$special": "unknown", "extra": "x" }""")]
    public void A_malformed_special_answer_is_refused(string json)
    {
        var issues = QuestionnaireEngine.ValidateForSave(Parsed(), Answers(("price", json)));
        Assert.Contains(issues, i => i.Key == "price");
    }

    [Fact]
    public void A_special_answer_counts_as_answered_so_it_never_blocks_completing()
    {
        var answers = Answers(("name", "\"Sunrise\""), ("price", Unknown), ("hours", Unknown));

        Assert.True(QuestionnaireEngine.IsAnswered(answers["price"]));
        Assert.Empty(QuestionnaireEngine.ValidateForComplete(Parsed(), answers));
    }

    [Fact]
    public void A_required_question_left_blank_still_blocks_completing()
    {
        var issues = QuestionnaireEngine.ValidateForComplete(Parsed(), Answers(("name", "\"Sunrise\"")));
        Assert.Contains(issues, i => i.Key == "price");
        Assert.Contains(issues, i => i.Key == "hours");
    }

    [Fact]
    public void A_special_answer_skips_the_required_fields_of_a_structured_question()
    {
        // Saying "I don't know" to the opening hours must not demand the Monday-to-Friday field inside it.
        var issues = QuestionnaireEngine.ValidateForComplete(Parsed(), Answers(("name", "\"Sunrise\""), ("price", "\"Rs 500\""), ("hours", """{ "$special": "not_applicable" }""")));
        Assert.Empty(issues);
    }

    [Fact]
    public void A_special_answer_gives_a_later_question_nothing_to_depend_on()
    {
        var def = Parsed();

        // "Do you have a branch?" answered "I don't know" is not an answer a condition can use.
        var visible = QuestionnaireEngine.VisibleKeys(def, Answers(("has_branch", Unknown)));
        Assert.DoesNotContain("branch_city", visible);
        Assert.DoesNotContain("after_branch", visible);

        Assert.Contains("after_branch", QuestionnaireEngine.VisibleKeys(def, Answers(("has_branch", "false"))));
    }

    [Fact]
    public void A_special_answer_produces_no_fact_and_is_reported_as_a_gap_to_follow_up()
    {
        var def = Parsed();
        var answers = Answers(("name", "\"Sunrise\""), ("price", """{ "$special": "need_help" }"""));

        var facts = QuestionnaireEngine.FactsFrom(def, answers);
        Assert.Single(facts);
        Assert.Equal("name", facts[0].SourceRef);

        var specials = QuestionnaireEngine.SpecialsFrom(def, answers);
        Assert.Equal(new[] { ("price", "need_help") }, specials);
    }

    [Fact]
    public void A_special_answer_to_a_question_that_is_no_longer_asked_is_dropped_like_any_other_answer()
    {
        var def = Parsed();
        var kept = QuestionnaireEngine.Prune(def, Answers(("has_branch", "false"), ("branch_city", Unknown)));

        Assert.True(kept.ContainsKey("has_branch"));
        Assert.False(kept.ContainsKey("branch_city"));
    }

    // ----- secrets -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("password: hunter2hunter2")]
    [InlineData("my api key is sk-abcdefghijklmnopqrstuvwx")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("token = ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("card 4111 1111 1111 1111 expiry 12/29")]
    [InlineData("4111-1111-1111-1111")]
    public void Text_that_looks_like_a_credential_is_recognised(string text) => Assert.True(QuestionnaireEngine.LooksLikeSecret(text));

    [Theory]
    [InlineData("Call us on 98765 43210")]
    [InlineData("We are at 12 MG Road, Pune - 411001")]
    [InlineData("Cataract surgery costs Rs 25,000 per eye")]
    [InlineData("Our PIN code is 411001 and the secret to our success is care")]
    [InlineData("Order number 1234567890123")] // 13 digits but not a card number (fails the Luhn check)
    [InlineData("")]
    public void Ordinary_business_text_is_not_mistaken_for_a_credential(string text) => Assert.False(QuestionnaireEngine.LooksLikeSecret(text));

    [Fact]
    public void A_secret_in_a_text_answer_or_a_structured_field_is_refused()
    {
        var def = Parsed();

        var plain = QuestionnaireEngine.ValidateForSave(def, Answers(("price", "\"password: hunter2hunter2\"")));
        Assert.Contains(plain, i => i.Key == "price" && i.Message == QuestionnaireEngine.SecretMessage);

        var structured = QuestionnaireEngine.ValidateForSave(def, Answers(("hours", """{ "weekdays": "api key = abcd1234efgh" }""")));
        Assert.Contains(structured, i => i.Key == "hours" && i.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    // ----- the new definition fields -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_new_definition_fields_are_read_and_checked()
    {
        var def = Parsed();
        var name = def.Questions[0];

        Assert.Equal("About", name.Group);
        Assert.Equal("Sunrise Clinic", name.Example);
        Assert.True(name.RequireKnown);
        Assert.Equal(180, def.Questions[1].Fact!.ValidDays);
        Assert.Empty(QuestionnaireEngine.Check(def));
    }

    [Fact]
    public void Require_known_without_required_is_a_mistake_in_the_template()
    {
        var def = new QuestionnaireDefinition { Questions = { new QuestionDef { Key = "q", Type = QuestionType.ShortText, Prompt = "Q?", RequireKnown = true } } };
        Assert.Contains(QuestionnaireEngine.Check(def), i => i.Message.Contains("RequireKnown"));
    }

    [Fact]
    public void Overlong_help_example_group_and_fact_details_are_refused_when_publishing()
    {
        var def = new QuestionnaireDefinition
        {
            Questions =
            {
                new QuestionDef
                {
                    Key = "q", Type = QuestionType.ShortText, Prompt = "Q?", HelpText = new string('h', 501), Example = new string('e', 301), Group = new string('g', 61),
                    Fact = new FactRule { Category = "About", Template = "{value}", Subject = new string('s', 121), Context = new string('c', 301), ValidDays = 0 }
                }
            }
        };

        var messages = string.Join("|", QuestionnaireEngine.Check(def).Select(i => i.Message));
        Assert.Contains("help text", messages);
        Assert.Contains("example", messages);
        Assert.Contains("group name", messages);
        Assert.Contains("subject", messages);
        Assert.Contains("context", messages);
        Assert.Contains("ValidDays", messages);
    }
}
