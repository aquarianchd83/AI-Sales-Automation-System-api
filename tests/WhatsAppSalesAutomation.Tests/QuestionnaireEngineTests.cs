using System.Text.Json;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The questionnaire rules with no database: which questions are asked, which answers are acceptable, what a definition must
/// look like, what facts the answers produce.</summary>
public sealed class QuestionnaireEngineTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Answers(params (string Key, string Json)[] answers) =>
        answers.ToDictionary(a => a.Key, a => J(a.Json), StringComparer.Ordinal);

    private const string Definition = """
    {
      "questions": [
        { "key": "name", "type": "ShortText", "prompt": "Name?", "required": true, "fact": { "category": "About", "template": "Called {value}." } },
        { "key": "has_surgery", "type": "Boolean", "prompt": "Surgery?" },
        { "key": "surgery_types", "type": "MultiChoice", "prompt": "Which?", "required": true,
          "options": [ { "value": "cataract", "label": "Cataract" }, { "value": "lasik", "label": "LASIK" }, { "value": "retina", "label": "Retina" } ],
          "showWhen": { "questionKey": "has_surgery", "operator": "Equal", "value": true },
          "fact": { "category": "Services", "template": "We do {value}." } },
        { "key": "lasik_price", "type": "Number", "prompt": "LASIK price?", "min": 0, "max": 500000,
          "showWhen": { "all": [
            { "questionKey": "has_surgery", "operator": "Equal", "value": true },
            { "questionKey": "surgery_types", "operator": "Contains", "value": "lasik" } ] } },
        { "key": "hours", "type": "Structured", "prompt": "Hours?",
          "fields": [ { "key": "weekdays", "label": "Mon-Fri", "required": true }, { "key": "saturday", "label": "Saturday" } ],
          "fact": { "category": "Contact", "template": "Open {value}." } },
        { "key": "size", "type": "SingleChoice", "prompt": "Size?",
          "options": [ { "value": "small", "label": "Small" }, { "value": "large", "label": "Large" } ] }
      ]
    }
    """;

    private static QuestionnaireDefinition Parsed() => QuestionnaireEngine.Parse(Definition);

    // ----- conditional questions ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_conditional_question_is_asked_only_when_its_condition_holds()
    {
        var def = Parsed();

        Assert.DoesNotContain("surgery_types", QuestionnaireEngine.VisibleKeys(def, Answers()));
        Assert.DoesNotContain("surgery_types", QuestionnaireEngine.VisibleKeys(def, Answers(("has_surgery", "false"))));
        Assert.Contains("surgery_types", QuestionnaireEngine.VisibleKeys(def, Answers(("has_surgery", "true"))));
    }

    [Fact]
    public void Hiding_a_question_hides_the_questions_that_depend_on_it()
    {
        var def = Parsed();
        var all = Answers(("has_surgery", "true"), ("surgery_types", "[\"lasik\"]"));
        Assert.Contains("lasik_price", QuestionnaireEngine.VisibleKeys(def, all));

        // Switching surgery off hides surgery_types, so its old answer no longer counts and lasik_price goes too.
        var switchedOff = Answers(("has_surgery", "false"), ("surgery_types", "[\"lasik\"]"));
        var visible = QuestionnaireEngine.VisibleKeys(def, switchedOff);
        Assert.DoesNotContain("surgery_types", visible);
        Assert.DoesNotContain("lasik_price", visible);
    }

    [Fact]
    public void All_needs_every_part_and_the_multiple_choice_must_include_the_value()
    {
        var def = Parsed();

        Assert.DoesNotContain("lasik_price", QuestionnaireEngine.VisibleKeys(def, Answers(("has_surgery", "true"), ("surgery_types", "[\"cataract\"]"))));
        Assert.Contains("lasik_price", QuestionnaireEngine.VisibleKeys(def, Answers(("has_surgery", "true"), ("surgery_types", "[\"cataract\",\"lasik\"]"))));
    }

    [Fact]
    public void A_hidden_questions_answer_is_never_kept()
    {
        var def = Parsed();
        var stale = Answers(("name", "\"Clinic\""), ("has_surgery", "false"), ("surgery_types", "[\"lasik\"]"), ("lasik_price", "1000"), ("nonsense", "\"x\""));

        var kept = QuestionnaireEngine.Prune(def, stale);

        Assert.Equal(new[] { "has_surgery", "name" }, kept.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void An_empty_answer_counts_as_no_answer()
    {
        var def = Parsed();
        var kept = QuestionnaireEngine.Prune(def, Answers(("name", "\"   \""), ("has_surgery", "false"), ("hours", "{\"weekdays\":\"\"}")));

        Assert.Equal(new[] { "has_surgery" }, kept.Keys.ToArray());
    }

    [Theory]
    [InlineData("Equal", "\"Yes\"", "\"yes\"", true)]
    [InlineData("NotEqual", "\"a\"", "\"b\"", true)]
    [InlineData("In", "\"b\"", "[\"a\",\"b\"]", true)]
    [InlineData("In", "\"c\"", "[\"a\",\"b\"]", false)]
    [InlineData("GreaterThan", "10", "5", true)]
    [InlineData("LessThan", "10", "5", false)]
    [InlineData("Contains", "\"Hello World\"", "\"world\"", true)]
    [InlineData("Answered", "\"x\"", "null", true)]
    public void Each_operator_compares_as_described(string op, string answer, string value, bool expected)
    {
        var condition = new ConditionDef
        {
            QuestionKey = "q",
            Operator = Enum.Parse<ConditionOperator>(op),
            Value = op == "Answered" ? null : J(value)
        };

        Assert.Equal(expected, QuestionnaireEngine.Evaluate(condition, Answers(("q", answer))));
    }

    [Fact]
    public void A_missing_answer_fails_every_comparison_except_the_negative_ones()
    {
        ConditionDef C(ConditionOperator op) => new() { QuestionKey = "q", Operator = op, Value = J("\"x\"") };
        var none = Answers();

        Assert.False(QuestionnaireEngine.Evaluate(C(ConditionOperator.Equal), none));
        Assert.False(QuestionnaireEngine.Evaluate(C(ConditionOperator.In), none));
        Assert.True(QuestionnaireEngine.Evaluate(C(ConditionOperator.NotEqual), none));
        Assert.True(QuestionnaireEngine.Evaluate(new ConditionDef { QuestionKey = "q", Operator = ConditionOperator.NotAnswered }, none));
        Assert.False(QuestionnaireEngine.Evaluate(new ConditionDef { QuestionKey = "q", Operator = ConditionOperator.Answered }, none));
    }

    [Fact]
    public void Any_needs_just_one_part()
    {
        var condition = new ConditionDef
        {
            Any = new()
            {
                new ConditionDef { QuestionKey = "a", Operator = ConditionOperator.Equal, Value = J("true") },
                new ConditionDef { QuestionKey = "b", Operator = ConditionOperator.Equal, Value = J("true") }
            }
        };

        Assert.True(QuestionnaireEngine.Evaluate(condition, Answers(("b", "true"))));
        Assert.False(QuestionnaireEngine.Evaluate(condition, Answers(("a", "false"), ("b", "false"))));
    }

    // ----- checking answers --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_hidden_required_question_is_never_required()
    {
        var def = Parsed();

        // surgery_types is required, but only when has_surgery is true.
        var issues = QuestionnaireEngine.ValidateForComplete(def, Answers(("name", "\"Clinic\""), ("has_surgery", "false"), ("hours", "{\"weekdays\":\"9-5\"}")));

        Assert.Empty(issues);
    }

    [Fact]
    public void A_visible_required_question_must_be_answered_to_complete()
    {
        var def = Parsed();

        var issues = QuestionnaireEngine.ValidateForComplete(def, Answers(("name", "\"Clinic\""), ("has_surgery", "true")));

        Assert.Contains(issues, i => i.Key == "surgery_types");
        Assert.DoesNotContain(issues, i => i.Key == "name");
    }

    [Fact]
    public void Saving_is_lenient_about_what_is_missing_but_strict_about_what_is_there()
    {
        var def = Parsed();

        Assert.Empty(QuestionnaireEngine.ValidateForSave(def, Answers()));
        Assert.Empty(QuestionnaireEngine.ValidateForSave(def, Answers(("name", "\"Half done\""))));
        Assert.Contains(QuestionnaireEngine.ValidateForSave(def, Answers(("has_surgery", "\"yes\""))), i => i.Key == "has_surgery");
        Assert.Contains(QuestionnaireEngine.ValidateForSave(def, Answers(("size", "\"huge\""))), i => i.Key == "size");
        Assert.Contains(QuestionnaireEngine.ValidateForSave(def, Answers(("surgery_types", "[\"lasik\",\"lasik\"]"))), i => i.Key == "surgery_types");
        Assert.Contains(QuestionnaireEngine.ValidateForSave(def, Answers(("lasik_price", "-5"))), i => i.Key == "lasik_price");
        Assert.Contains(QuestionnaireEngine.ValidateForSave(def, Answers(("missing_question", "1"))), i => i.Key == "missing_question");
    }

    [Fact]
    public void A_structured_answer_needs_its_required_fields_and_only_its_own_fields()
    {
        var def = Parsed();

        var missing = QuestionnaireEngine.ValidateForComplete(def, Answers(("name", "\"C\""), ("has_surgery", "false"), ("hours", "{\"saturday\":\"9-1\"}")));
        Assert.Contains(missing, i => i.Key == "hours.weekdays");

        var stranger = QuestionnaireEngine.ValidateForSave(def, Answers(("hours", "{\"weekdays\":\"9-5\",\"sunday\":\"closed\"}")));
        Assert.Contains(stranger, i => i.Key == "hours");
    }

    [Fact]
    public void Text_answers_respect_their_length_limit()
    {
        var def = Parsed();

        var tooLong = new string('x', QuestionnaireEngine.DefaultShortTextLength + 1);
        Assert.Contains(QuestionnaireEngine.ValidateForSave(def, Answers(("name", JsonSerializer.Serialize(tooLong)))), i => i.Key == "name");
    }

    // ----- facts -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Answers_become_facts_in_words_with_option_labels()
    {
        var def = Parsed();

        var facts = QuestionnaireEngine.FactsFrom(def, Answers(
            ("name", "\"  Trilok Eye  \""), ("has_surgery", "true"), ("surgery_types", "[\"cataract\",\"lasik\",\"retina\"]"),
            ("hours", "{\"weekdays\":\"9-6\",\"saturday\":\"9-1\"}")));

        Assert.Equal("Called Trilok Eye.", facts.Single(f => f.SourceRef == "name").Statement);
        Assert.Equal("We do Cataract, LASIK and Retina.", facts.Single(f => f.SourceRef == "surgery_types").Statement);
        Assert.Equal("Open Mon-Fri: 9-6; Saturday: 9-1.", facts.Single(f => f.SourceRef == "hours").Statement);
        Assert.Equal("Services", facts.Single(f => f.SourceRef == "surgery_types").Category);
    }

    [Fact]
    public void A_hidden_or_unanswered_question_states_no_fact()
    {
        var def = Parsed();

        var facts = QuestionnaireEngine.FactsFrom(def, Answers(("name", "\"A\""), ("has_surgery", "false"), ("surgery_types", "[\"lasik\"]")));

        Assert.Equal(new[] { "name" }, facts.Select(f => f.SourceRef).ToArray());
    }

    // ----- the definition itself ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_starter_template_is_itself_a_valid_definition()
    {
        var def = QuestionnaireEngine.Parse(QuestionnaireEngine.StarterDefinitionJson());

        Assert.True(def.Questions.Count >= 5);
        Assert.Contains(def.Questions, q => q.ShowWhen is not null);
        Assert.Contains(def.Questions, q => q.Type == QuestionType.Structured);
        Assert.Contains(def.Questions, q => q.PrefillFrom is not null);
    }

    [Fact]
    public void A_definition_survives_being_saved_and_read_back()
    {
        var again = QuestionnaireEngine.Parse(QuestionnaireEngine.Serialize(Parsed()));

        Assert.Equal(Parsed().Questions.Select(q => q.Key), again.Questions.Select(q => q.Key));
        Assert.Equal(ConditionOperator.Equal, again.Questions.Single(q => q.Key == "surgery_types").ShowWhen!.Operator);
    }

    [Theory]
    [InlineData("""{ "questions": [] }""", "at least one question")]
    [InlineData("""{ "questions": [ { "key": "Bad Key", "type": "ShortText", "prompt": "x" } ] }""", "key must start")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "ShortText", "prompt": "x" }, { "key": "a", "type": "ShortText", "prompt": "y" } ] }""", "more than one question")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "ShortText", "prompt": "" } ] }""", "needs a prompt")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "SingleChoice", "prompt": "x" } ] }""", "at least one option")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "Structured", "prompt": "x" } ] }""", "at least one field")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "ShortText", "prompt": "x", "options": [ { "value": "v", "label": "V" } ] } ] }""", "Only a choice question")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "ShortText", "prompt": "x", "showWhen": { "questionKey": "a", "operator": "Equal", "value": 1 } } ] }""", "not an earlier question")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "ShortText", "prompt": "x", "showWhen": { "questionKey": "b", "operator": "Equal", "value": 1 } }, { "key": "b", "type": "ShortText", "prompt": "y" } ] }""", "not an earlier question")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "Boolean", "prompt": "x" }, { "key": "b", "type": "ShortText", "prompt": "y", "showWhen": { "questionKey": "a", "operator": "GreaterThan", "value": 1 } } ] }""", "only works on a number")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "Boolean", "prompt": "x" }, { "key": "b", "type": "ShortText", "prompt": "y", "showWhen": { "questionKey": "a", "operator": "Equal" } } ] }""", "needs a Value")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "ShortText", "prompt": "x", "fact": { "category": "C", "template": "no placeholder" } } ] }""", "must contain {value}")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "Number", "prompt": "x", "prefillFrom": "Name" } ] }""", "text question")]
    [InlineData("""{ "questions": [ { "key": "a", "type": "ShortText", "prompt": "x", "prefillFrom": "Password" } ] }""", "PrefillFrom must be")]
    [InlineData("not json at all", "Not valid JSON")]
    public void A_bad_definition_is_refused_with_a_message_that_says_what_is_wrong(string json, string expected)
    {
        var (definition, issues) = QuestionnaireEngine.TryParse(json);

        Assert.Null(definition);
        Assert.Contains(issues, i => i.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var (_, issues) = QuestionnaireEngine.TryParse("""{ "questions": [ { "key": "a", "type": "SingleChoice", "prompt": "" }, { "key": "a", "type": "Structured", "prompt": "ok" } ] }""");

        Assert.True(issues.Count >= 4);
    }
}
