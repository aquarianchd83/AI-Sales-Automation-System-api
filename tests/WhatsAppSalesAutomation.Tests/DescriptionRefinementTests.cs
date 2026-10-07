using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Tenancy;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>"AI suggest" on the business description: the tenant's own AI rewrites what they wrote when it has one, and
/// otherwise the text is only tidied - and the answer always says which. The speciality (Eye clinic under Healthcare)
/// reaches both this and the keyword suggestions.</summary>
public sealed class DescriptionRefinementTests
{
    private sealed class FakeAi : IAiTextGenerator
    {
        public string? Reply { get; set; }
        public string? LastPrompt { get; private set; }

        public Task<string?> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default)
        {
            LastPrompt = userPrompt;
            return Task.FromResult(Reply);
        }
    }

    private readonly FakeAi _ai = new();

    private DescriptionRefinementService Service() => new(_ai, new RefineDescriptionRequestValidator());

    [Fact]
    public async Task Uses_the_tenants_AI_when_it_answers_and_says_so()
    {
        _ai.Reply = "We run an eye clinic in Mohali offering cataract surgery and LASIK.";

        var result = await Service().RefineAsync(new RefineDescriptionRequest("eye clinic mohali cataract lasik", "Healthcare", "Eye clinic"));

        Assert.Equal("AI", result.Source);
        Assert.Equal("We run an eye clinic in Mohali offering cataract surgery and LASIK.", result.Description);
    }

    [Fact]
    public async Task Gives_the_AI_the_draft_the_industry_and_the_speciality()
    {
        _ai.Reply = "Better.";

        await Service().RefineAsync(new RefineDescriptionRequest("eye clinic mohali", "Healthcare", "Eye clinic"));

        Assert.Contains("Industry: Healthcare", _ai.LastPrompt);
        Assert.Contains("Speciality: Eye clinic", _ai.LastPrompt);
        Assert.Contains("Eye clinic mohali.", _ai.LastPrompt); // the tidied draft
    }

    [Fact]
    public async Task Only_tidies_the_text_when_the_AI_gives_nothing_and_says_so()
    {
        _ai.Reply = null;

        var result = await Service().RefineAsync(new RefineDescriptionRequest("  we sell  solar panels ,and inverters ", null, null));

        Assert.Equal("Tidied", result.Source);
        Assert.Equal("We sell solar panels, and inverters.", result.Description);
    }

    [Fact]
    public async Task Strips_a_code_fence_and_wrapping_quotes_from_the_AI_reply()
    {
        _ai.Reply = "```text\n\"Rooftop solar for homes.\"\n```";

        var result = await Service().RefineAsync(new RefineDescriptionRequest("solar", null, null));

        Assert.Equal("Rooftop solar for homes.", result.Description);
    }

    [Fact]
    public async Task Never_returns_more_than_the_field_holds()
    {
        _ai.Reply = string.Concat(Enumerable.Repeat("A sentence that goes on. ", 200));

        var result = await Service().RefineAsync(new RefineDescriptionRequest("solar", null, null));

        Assert.True(result.Description.Length <= TenantProfileLimits.BusinessDescription);
        Assert.EndsWith(".", result.Description);
    }

    [Fact]
    public async Task Asks_for_a_draft_when_there_is_nothing_to_refine()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Service().RefineAsync(new RefineDescriptionRequest("   ", "Healthcare", null)));
    }

    [Fact]
    public async Task Keyword_suggestions_are_for_the_speciality_not_the_whole_industry()
    {
        _ai.Reply = "[]";
        var keywords = new KeywordSuggestionService(_ai, new SuggestKeywordsRequestValidator());

        await keywords.SuggestAsync(new SuggestKeywordsRequest("Healthcare", null, null, "Eye clinic"));

        Assert.Contains("Speciality within it: Eye clinic", _ai.LastPrompt);
    }
}
