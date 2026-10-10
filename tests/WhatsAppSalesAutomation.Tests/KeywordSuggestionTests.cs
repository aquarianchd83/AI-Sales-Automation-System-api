using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Tenancy;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>"AI suggest" on the Business Profile: keywords for the chosen industry, from the tenant's own AI when it has
/// one and from common terms when it does not - and always saying which.</summary>
public sealed class KeywordSuggestionTests
{
    private sealed class FakeAi : IAiTextGenerator
    {
        public string? Reply { get; set; }
        public string? LastPrompt { get; private set; }

        public Task<string?> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default, string? provider = null)
        {
            LastPrompt = userPrompt;
            return Task.FromResult(Reply);
        }
    }

    private readonly FakeAi _ai = new();

    private KeywordSuggestionService Service() => new(_ai, new SuggestKeywordsRequestValidator());

    [Fact]
    public async Task Uses_the_tenants_AI_when_it_answers_and_says_so()
    {
        _ai.Reply = """["rooftop solar", "net metering", "solar inverter"]""";

        var result = await Service().SuggestAsync(new SuggestKeywordsRequest("Solar & energy", null, null));

        Assert.Equal("AI", result.Source);
        Assert.Equal(new[] { "rooftop solar", "net metering", "solar inverter" }, result.Keywords);
    }

    [Fact]
    public async Task Tells_the_AI_the_industry_the_description_and_what_to_leave_out()
    {
        _ai.Reply = "[]";

        await Service().SuggestAsync(new SuggestKeywordsRequest("Healthcare", "A dental clinic in Mohali.", new[] { "dental care" }));

        Assert.Contains("Industry: Healthcare", _ai.LastPrompt);
        Assert.Contains("A dental clinic in Mohali.", _ai.LastPrompt);
        Assert.Contains("do not repeat): dental care", _ai.LastPrompt);
    }

    [Fact]
    public async Task Understands_a_reply_wrapped_in_a_code_fence_or_a_sentence()
    {
        _ai.Reply = "Sure! Here you go:\n```json\n[\"solar panels\", \"installation\"]\n```";

        var result = await Service().SuggestAsync(new SuggestKeywordsRequest("Solar & energy", null, null));

        Assert.Equal("AI", result.Source);
        Assert.Equal(new[] { "solar panels", "installation" }, result.Keywords);
    }

    [Fact]
    public async Task Cleans_what_the_AI_returns_the_way_the_profile_cleans_keywords()
    {
        _ai.Reply = """["  Solar   Panels ", "solar panels", "", "dental care", "this keyword is far too long to be accepted as a domain keyword at all", "net metering"]""";

        var result = await Service().SuggestAsync(new SuggestKeywordsRequest("Solar", null, new[] { "Dental Care" }));

        Assert.Equal(new[] { "Solar Panels", "net metering" }, result.Keywords); // trimmed, de-duplicated, not already added, short enough
    }

    [Fact]
    public async Task Never_returns_more_than_twelve()
    {
        _ai.Reply = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, 30).Select(i => $"keyword {i}"));

        var result = await Service().SuggestAsync(new SuggestKeywordsRequest("Anything", null, null));

        Assert.Equal(12, result.Keywords.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I cannot help with that.")]
    [InlineData("[not json")]
    [InlineData("[1, 2, 3]")]
    public async Task Falls_back_to_common_terms_when_there_is_no_usable_AI_answer(string? reply)
    {
        _ai.Reply = reply;

        var result = await Service().SuggestAsync(new SuggestKeywordsRequest("Solar & energy", null, null));

        Assert.Equal("Common terms", result.Source);
        Assert.Contains("solar panels", result.Keywords);
    }

    [Fact]
    public async Task Common_terms_leave_out_what_the_tenant_already_has()
    {
        var result = await Service().SuggestAsync(new SuggestKeywordsRequest("Solar & energy", null, new[] { "Solar Panels", "installation" }));

        Assert.DoesNotContain("solar panels", result.Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("installation", result.Keywords);
    }

    [Fact]
    public async Task An_industry_nobody_has_heard_of_gets_no_suggestions_rather_than_wrong_ones()
    {
        var result = await Service().SuggestAsync(new SuggestKeywordsRequest("Underwater basket weaving", null, null));

        Assert.Equal("Common terms", result.Source);
        Assert.Empty(result.Keywords);
    }

    [Fact]
    public async Task An_industry_is_required()
    {
        var refused = await Assert.ThrowsAsync<ValidationException>(() => Service().SuggestAsync(new SuggestKeywordsRequest("  ", null, null)));

        Assert.Contains(refused.Errors, e => e.ErrorMessage == "Choose an industry first.");
    }

    [Theory]
    [InlineData("Healthcare", "appointment booking")]
    [InlineData("healthcare services", "appointment booking")]
    [InlineData("Dental clinic", "appointment booking")]
    [InlineData("Car dealership", "test drive")]
    [InlineData("Software company", "custom software")]
    [InlineData("Restaurant", "dine in")]
    [InlineData("Real estate", "apartments")]
    public void The_catalog_matches_an_industry_by_name_or_by_a_whole_word_in_it(string industry, string expected)
    {
        Assert.Contains(expected, IndustryKeywordCatalog.For(industry));
    }

    [Fact]
    public void The_catalog_does_not_match_part_of_a_word()
    {
        // "healthcare" contains "car" - it must still be healthcare, not automotive.
        Assert.DoesNotContain("test drive", IndustryKeywordCatalog.For("Healthcare"));
        Assert.Empty(IndustryKeywordCatalog.For("Carpentry workshop"));
    }

    [Fact]
    public void Every_industry_the_profile_suggests_has_common_terms()
    {
        foreach (var industry in new[]
                 {
                     "Automotive", "Education", "Financial services", "Food & beverage", "Healthcare", "Real estate",
                     "Retail & e-commerce", "Solar & energy", "Technology & software", "Travel & hospitality"
                 })
        {
            Assert.NotEmpty(IndustryKeywordCatalog.For(industry));
        }
    }
}
