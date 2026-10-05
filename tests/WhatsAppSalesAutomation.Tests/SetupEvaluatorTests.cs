using System.Text.Json;
using WhatsAppSalesAutomation.Application.Setup;
using WhatsAppSalesAutomation.Domain.Entities.Setup;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The rules that decide which questions apply, what is missing and what is wrong - the heart of the
/// plan-driven setup, and pure functions, so tested without a database.</summary>
public sealed class SetupEvaluatorTests
{
    private static PlanRequirement Field(
        string key, SetupFieldType type = SetupFieldType.Text, bool required = false, string? whenKey = null,
        SetupConditionOperator whenOp = SetupConditionOperator.Equals, string? whenValue = null,
        string[]? options = null, SetupValidationDto? rules = null, string section = "business", int order = 0) => new()
    {
        FieldKey = key,
        Label = key.Replace('_', ' '),
        FieldType = type,
        IsRequired = required,
        Section = section,
        DisplayOrder = order,
        ConditionFieldKey = whenKey,
        ConditionOperator = whenKey is null ? null : whenOp,
        ConditionValue = whenValue,
        OptionsJson = SetupJson.WriteOptions(options?.Select(o => new SetupOptionDto(o, o.ToUpperInvariant())).ToList()),
        ValidationJson = SetupJson.WriteValidation(rules)
    };

    private static Dictionary<string, string?> Answers(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static SetupEvaluation Run(IEnumerable<PlanRequirement> fields, params (string Key, string? Value)[] pairs) =>
        SetupEvaluator.Evaluate(fields.ToList(), Answers(pairs));

    [Fact]
    public void A_required_field_with_no_answer_is_missing_and_blocks_completion()
    {
        var result = Run(new[] { Field("brand_name", required: true), Field("website") });

        Assert.False(result.IsComplete);
        var missing = Assert.Single(result.Missing);
        Assert.Equal("brand_name", missing.FieldKey);
        Assert.Contains("required", missing.Message);
    }

    [Fact]
    public void An_optional_field_may_stay_empty()
    {
        Assert.True(Run(new[] { Field("website", SetupFieldType.Url) }).IsComplete);
    }

    [Fact]
    public void Inactive_fields_are_ignored()
    {
        var inactive = Field("old_question", required: true);
        inactive.IsActive = false;

        var result = Run(new[] { inactive });

        Assert.True(result.IsComplete);
        Assert.Empty(result.VisibleKeys);
    }

    [Fact]
    public void A_conditional_field_is_hidden_and_not_required_until_its_condition_is_met()
    {
        var fields = new[]
        {
            Field("run_ads", SetupFieldType.Radio, required: true, options: new[] { "yes", "no" }),
            Field("ad_budget", SetupFieldType.Currency, required: true, whenKey: "run_ads", whenValue: "yes"),
        };

        var no = Run(fields, ("run_ads", "no"));
        Assert.True(no.IsComplete);
        Assert.DoesNotContain("ad_budget", no.VisibleKeys);

        var yes = Run(fields, ("run_ads", "yes"));
        Assert.False(yes.IsComplete);
        Assert.Contains("ad_budget", yes.VisibleKeys);
        Assert.Equal("ad_budget", Assert.Single(yes.Missing).FieldKey);
    }

    [Fact]
    public void A_hidden_parent_hides_its_children_even_when_the_childs_own_condition_matches()
    {
        var fields = new[]
        {
            Field("run_ads", SetupFieldType.Radio, options: new[] { "yes", "no" }),
            Field("ad_platform", SetupFieldType.Dropdown, whenKey: "run_ads", whenValue: "yes", options: new[] { "meta", "google" }),
            Field("meta_pixel", required: true, whenKey: "ad_platform", whenValue: "meta"),
        };

        var result = Run(fields, ("run_ads", "no"), ("ad_platform", "meta"));

        Assert.Equal(new[] { "run_ads" }, result.VisibleKeys);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void A_multi_select_condition_matches_when_the_option_is_among_the_picks()
    {
        var fields = new[]
        {
            Field("platforms", SetupFieldType.MultiSelect, required: true, options: new[] { "instagram", "facebook" }),
            Field("instagram_handle", required: true, whenKey: "platforms", whenOp: SetupConditionOperator.Contains, whenValue: "instagram"),
            Field("facebook_page", required: true, whenKey: "platforms", whenOp: SetupConditionOperator.Contains, whenValue: "facebook"),
        };

        var result = Run(fields, ("platforms", SetupJson.WriteList(new[] { "instagram" })));

        Assert.Contains("instagram_handle", result.VisibleKeys);
        Assert.DoesNotContain("facebook_page", result.VisibleKeys);
        Assert.Equal("instagram_handle", Assert.Single(result.Missing).FieldKey);
    }

    [Theory]
    [InlineData(SetupConditionOperator.Equals, "a", "a", true)]
    [InlineData(SetupConditionOperator.Equals, "A", "a", true)]
    [InlineData(SetupConditionOperator.NotEquals, "a", "b", true)]
    [InlineData(SetupConditionOperator.NotEquals, "a", "a", false)]
    [InlineData(SetupConditionOperator.In, "b", "a, b ,c", true)]
    [InlineData(SetupConditionOperator.In, "z", "a,b,c", false)]
    [InlineData(SetupConditionOperator.NotEmpty, "x", null, true)]
    [InlineData(SetupConditionOperator.NotEmpty, null, null, false)]
    [InlineData(SetupConditionOperator.Empty, null, null, true)]
    [InlineData(SetupConditionOperator.Contains, "hello world", "world", true)]
    public void Condition_operators(SetupConditionOperator op, string? answer, string? expected, bool matches)
    {
        Assert.Equal(matches, SetupEvaluator.ConditionMatches(op, answer, expected));
    }

    [Fact]
    public void A_condition_on_an_unknown_field_or_a_cycle_hides_the_field_instead_of_requiring_it()
    {
        var orphan = Field("orphan", required: true, whenKey: "nothing", whenValue: "x");
        var a = Field("a", required: true, whenKey: "b", whenValue: "x");
        var b = Field("b", required: true, whenKey: "a", whenValue: "x");

        var result = Run(new[] { orphan, a, b }, ("a", "x"), ("b", "x"));

        Assert.Empty(result.VisibleKeys);
        Assert.True(result.IsComplete);
    }

    [Theory]
    [InlineData(SetupFieldType.Email, "not-an-email", false)]
    [InlineData(SetupFieldType.Email, "owner@example.com", true)]
    [InlineData(SetupFieldType.Url, "example.com", false)]
    [InlineData(SetupFieldType.Url, "ftp://example.com", false)]
    [InlineData(SetupFieldType.Url, "https://example.com/page", true)]
    [InlineData(SetupFieldType.Number, "12.5", false)]
    [InlineData(SetupFieldType.Number, "12", true)]
    [InlineData(SetupFieldType.Decimal, "12.5", true)]
    [InlineData(SetupFieldType.Decimal, "abc", false)]
    [InlineData(SetupFieldType.Currency, "-1", false)]
    [InlineData(SetupFieldType.Currency, "0", true)]
    [InlineData(SetupFieldType.Currency, "25000.50", true)]
    [InlineData(SetupFieldType.Phone, "+91 98765 43210", true)]
    [InlineData(SetupFieldType.Phone, "12", false)]
    [InlineData(SetupFieldType.Phone, "call me", false)]
    [InlineData(SetupFieldType.Date, "2026-10-05", true)]
    [InlineData(SetupFieldType.Date, "05/10/2026", false)]
    [InlineData(SetupFieldType.Checkbox, "true", true)]
    [InlineData(SetupFieldType.Checkbox, "maybe", false)]
    public void Values_are_checked_against_their_field_type(SetupFieldType type, string answer, bool valid)
    {
        var result = Run(new[] { Field("f", type) }, ("f", answer));

        Assert.Equal(valid, result.IsComplete);
        if (!valid)
            Assert.Equal("f", Assert.Single(result.Invalid).FieldKey);
    }

    [Fact]
    public void Number_ranges_and_text_lengths_come_from_the_validation_rules()
    {
        var number = Field("hours", SetupFieldType.Number, rules: new SetupValidationDto(1, 720, null, null, null, null));
        var text = Field("about", SetupFieldType.MultilineText, rules: new SetupValidationDto(null, null, 10, 20, null, null));

        Assert.False(Run(new[] { number }, ("hours", "0")).IsComplete);
        Assert.False(Run(new[] { number }, ("hours", "721")).IsComplete);
        Assert.True(Run(new[] { number }, ("hours", "24")).IsComplete);
        Assert.False(Run(new[] { text }, ("about", "short")).IsComplete);
        Assert.False(Run(new[] { text }, ("about", new string('x', 21))).IsComplete);
        Assert.True(Run(new[] { text }, ("about", new string('x', 15))).IsComplete);
    }

    [Fact]
    public void A_pattern_rule_uses_the_admins_message_and_a_broken_pattern_never_blocks_anyone()
    {
        var good = Field("code", rules: new SetupValidationDto(null, null, null, null, "^[A-Z]{3}$", "Use three capital letters."));
        var broken = Field("code2", rules: new SetupValidationDto(null, null, null, null, "([", null));

        var bad = Run(new[] { good }, ("code", "ab"));
        Assert.Equal("Use three capital letters.", Assert.Single(bad.Invalid).Message);
        Assert.True(Run(new[] { good }, ("code", "ABC")).IsComplete);
        Assert.True(Run(new[] { broken }, ("code2", "anything")).IsComplete);
    }

    [Fact]
    public void Choice_answers_must_come_from_the_options_and_a_required_multi_select_needs_a_pick()
    {
        var drop = Field("objective", SetupFieldType.Dropdown, required: true, options: new[] { "sales", "reach" });
        var multi = Field("types", SetupFieldType.MultiSelect, required: true, options: new[] { "images", "videos" });

        Assert.Equal("objective", Assert.Single(Run(new[] { drop }, ("objective", "world domination")).Invalid).FieldKey);
        Assert.True(Run(new[] { drop }, ("objective", "SALES")).IsComplete);

        Assert.Equal("types", Assert.Single(Run(new[] { multi }, ("types", "[]")).Missing).FieldKey);
        Assert.Equal("types", Assert.Single(Run(new[] { multi }, ("types", SetupJson.WriteList(new[] { "images", "gifs" }))).Invalid).FieldKey);
        Assert.True(Run(new[] { multi }, ("types", SetupJson.WriteList(new[] { "images", "videos" }))).IsComplete);
    }

    [Fact]
    public void A_required_checkbox_must_be_ticked_and_a_required_file_must_be_uploaded()
    {
        var consent = Field("consent", SetupFieldType.Checkbox, required: true);
        var file = Field("brochure", SetupFieldType.FileUpload, required: true);

        Assert.False(Run(new[] { consent }, ("consent", "false")).IsComplete);
        Assert.True(Run(new[] { consent }, ("consent", "true")).IsComplete);
        Assert.Contains("Upload", Assert.Single(Run(new[] { file }).Missing).Message);
        Assert.True(Run(new[] { file }, ("brochure", "media-asset-id")).IsComplete);
    }

    [Fact]
    public void Progress_counts_only_valid_answers_to_visible_required_fields_and_orders_sections_as_a_wizard()
    {
        var fields = new[]
        {
            Field("campaign_goal", required: true, section: "campaign", order: 2000),
            Field("brand", required: true, section: "business", order: 1000),
            Field("email", SetupFieldType.Email, required: true, section: "business", order: 1010),
            Field("hidden", required: true, whenKey: "brand", whenValue: "never", section: "audience", order: 1500),
        };

        var result = Run(fields, ("brand", "Acme"), ("email", "nope"));

        Assert.Equal(33, result.PercentComplete); // 1 of 3 visible required answered validly
        Assert.Equal(new[] { "business", "campaign" }, result.Sections.Select(s => s.Key));
        Assert.False(result.Sections[0].IsComplete);
        Assert.True(result.Sections[0].Started);
        Assert.False(result.Sections[1].Started);
    }

    [Fact]
    public void Codec_normalises_raw_json_answers_to_the_stored_form_and_back()
    {
        JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        Assert.Null(SetupValueCodec.Normalize(Parse("null"), SetupFieldType.Text).Value);
        Assert.Null(SetupValueCodec.Normalize(Parse("\"  \""), SetupFieldType.Text).Value);
        Assert.Equal("hello", SetupValueCodec.Normalize(Parse("\"  hello \""), SetupFieldType.Text).Value);
        Assert.Equal("25000.5", SetupValueCodec.Normalize(Parse("25000.5"), SetupFieldType.Currency).Value);
        Assert.Equal("true", SetupValueCodec.Normalize(Parse("true"), SetupFieldType.Checkbox).Value);
        Assert.Equal("[\"a\",\"b\"]", SetupValueCodec.Normalize(Parse("[\"a\",\"A\",\"b\"]"), SetupFieldType.MultiSelect).Value);
        Assert.Equal("[\"a\"]", SetupValueCodec.Normalize(Parse("\"a\""), SetupFieldType.MultiSelect).Value);
        Assert.NotNull(SetupValueCodec.Normalize(Parse("{}"), SetupFieldType.Text).Error);

        Assert.Equal(new[] { "a", "b" }, (IEnumerable<string>)SetupValueCodec.ToTyped("[\"a\",\"b\"]", SetupFieldType.MultiSelect)!);
        Assert.Equal(true, SetupValueCodec.ToTyped("true", SetupFieldType.Checkbox));
        Assert.Equal(25000.5m, SetupValueCodec.ToTyped("25000.5", SetupFieldType.Currency));
    }

    [Fact]
    public void Projection_turns_tagged_answers_into_revenue_cost_profit_and_roi_without_inventing_numbers()
    {
        var price = Field("price", SetupFieldType.Currency); price.MetricKey = SetupMetrics.PackagePrice;
        var customers = Field("customers", SetupFieldType.Number); customers.MetricKey = SetupMetrics.ExpectedCustomers;
        var budget = Field("budget", SetupFieldType.Currency); budget.MetricKey = SetupMetrics.MarketingCost;
        var ads = Field("ads", SetupFieldType.Currency); ads.MetricKey = SetupMetrics.SocialMediaCost;
        var all = new[] { price, customers, budget, ads };

        var full = SetupProjectionCalculator.Calculate(all, Answers(("price", "5000"), ("customers", "10"), ("budget", "20000"), ("ads", "5000")));
        Assert.Equal(50000m, full.ExpectedRevenue);
        Assert.Equal(25000m, full.TotalCost);
        Assert.Equal(25000m, full.ExpectedProfit);
        Assert.Equal(100m, full.RoiPercent);

        var partial = SetupProjectionCalculator.Calculate(all, Answers(("budget", "20000")));
        Assert.Null(partial.ExpectedRevenue);
        Assert.Null(partial.ExpectedProfit);
        Assert.Equal(20000m, partial.TotalCost);
        Assert.Equal(new[] { "Package price", "Expected customers" }, partial.MissingInputs);
    }
}

/// <summary>The setup screens are driven by enum values, so they must cross the API by name and be accepted by name.</summary>
public sealed class SetupWireFormatTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Enums_are_written_as_names()
    {
        var field = new SetupFieldDto(Guid.NewGuid(), "k", "K", null, SetupFieldType.Currency, true, null,
            Array.Empty<SetupOptionDto>(), null, 1, "business",
            new SetupConditionDto("other", SetupConditionOperator.NotEmpty, null), null, true);

        var json = JsonSerializer.Serialize(field, Web);

        Assert.Contains("\"fieldType\":\"Currency\"", json);
        Assert.Contains("\"operator\":\"NotEmpty\"", json);
    }

    [Fact]
    public void Requests_accept_enum_names_and_a_save_request_accepts_mixed_json_values()
    {
        var requirement = JsonSerializer.Deserialize<SaveRequirementRequest>(
            "{\"fieldKey\":\"a_b\",\"label\":\"A\",\"fieldType\":\"MultiSelect\",\"isRequired\":true,\"displayOrder\":1,\"section\":\"x\",\"isActive\":true," +
            "\"condition\":{\"fieldKey\":\"c\",\"operator\":\"Contains\",\"value\":\"v\"}}", Web)!;
        Assert.Equal(SetupFieldType.MultiSelect, requirement.FieldType);
        Assert.Equal(SetupConditionOperator.Contains, requirement.Condition!.Operator);

        var save = JsonSerializer.Deserialize<SaveSetupRequest>(
            "{\"values\":{\"a\":\"text\",\"b\":12.5,\"c\":true,\"d\":[\"x\",\"y\"],\"e\":null},\"complete\":true}", Web)!;
        Assert.True(save.Complete);
        Assert.Equal(5, save.Values!.Count);
        Assert.Equal(JsonValueKind.Null, save.Values["e"].ValueKind);
    }
}
