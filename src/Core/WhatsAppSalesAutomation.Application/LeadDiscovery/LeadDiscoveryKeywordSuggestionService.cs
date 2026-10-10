using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Tenancy;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>Body of the lead discovery keyword suggestion request: the description the tenant wrote on the profile, the
/// business type they are looking for if they have said, and the keywords already added (never suggested again).</summary>
public record SuggestLeadKeywordsRequest(string Description, string? TargetBusinessType, IReadOnlyList<string>? Existing);

public interface ILeadDiscoveryKeywordSuggestionService
{
    /// <summary>The words people type into a search engine to find the kind of business the tenant is looking for. Reuses
    /// <see cref="KeywordSuggestionsDto"/>: <c>Source</c> is "AI" or "Common terms", never passed off as the other.</summary>
    Task<KeywordSuggestionsDto> SuggestAsync(SuggestLeadKeywordsRequest request, CancellationToken cancellationToken = default);
}

public class SuggestLeadKeywordsRequestValidator : AbstractValidator<SuggestLeadKeywordsRequest>
{
    public SuggestLeadKeywordsRequestValidator()
    {
        RuleFor(x => x.Description).NotEmpty().WithMessage("Write a description first - the suggestions are based on it.")
            .MaximumLength(LeadDiscoveryLimits.Description);
        RuleFor(x => x.TargetBusinessType).MaximumLength(LeadDiscoveryLimits.TargetBusinessType);
        RuleFor(x => x.Existing).Must(e => e is null || e.Count <= LeadDiscoveryLimits.MaxKeywords * 2);
    }
}

/// <summary>
/// "AI suggest" on the lead discovery profile: the search terms a person would type into Google to find the kind of
/// business the tenant is after - for an eye clinic "eye hospital", "eye doctor", "ophthalmologist", "optometrist", "vision
/// centre", and the way it is said locally. They are drawn from the target business type and the description, not from
/// what the tenant sells and nothing from the company profile: only the target business type and the description written
/// on the lead discovery profile itself. Asks the tenant's AI provider
/// when one is really configured; otherwise (Simulated, no key, or the call failed) falls back to the target type and the
/// common terms for it, and says so. Cleaned like any keyword list, so every suggestion can be added as is. Nothing is saved.
/// </summary>
public class LeadDiscoveryKeywordSuggestionService : ILeadDiscoveryKeywordSuggestionService
{
    public const int MaxSuggestions = 15;

    /// <summary>Lead discovery keywords are always written by ChatGPT (the OpenAI key saved under Configuration > AI Providers), whichever provider
    /// the platform uses for customer conversations. With no OpenAI key the suggestions fall back to the built-in ones.</summary>
    private const string AiProviderName = "OpenAI";

    private const string SystemPrompt =
        "You are a local-search expert. Reply with ONLY a JSON array of strings - no prose, no code fences.";

    private readonly IAiTextGenerator _ai;
    private readonly IValidator<SuggestLeadKeywordsRequest> _validator;

    public LeadDiscoveryKeywordSuggestionService(IAiTextGenerator ai, IValidator<SuggestLeadKeywordsRequest> validator)
    {
        _ai = ai;
        _validator = validator;
    }

    public async Task<KeywordSuggestionsDto> SuggestAsync(SuggestLeadKeywordsRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var existing = new HashSet<string>(TenantBusinessDetails.NormalizeKeywords(request.Existing), StringComparer.OrdinalIgnoreCase);

        var reply = await _ai.GenerateAsync(SystemPrompt, BuildPrompt(request, existing), 500, cancellationToken, provider: AiProviderName);
        var fromAi = Clean(KeywordSuggestionService.ParseList(reply), existing);
        if (fromAi.Count > 0)
            return new KeywordSuggestionsDto(fromAi, "AI");

        var known = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.TargetBusinessType))
            known.Add(request.TargetBusinessType);
        known.AddRange(IndustryKeywordCatalog.For(request.TargetBusinessType));
        return new KeywordSuggestionsDto(Clean(known, existing), "Common terms");
    }

    private static string BuildPrompt(SuggestLeadKeywordsRequest request, IReadOnlyCollection<string> existing)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.TargetBusinessType))
            lines.Add($"Kind of business to find: {request.TargetBusinessType.Trim()}");
        lines.Add($"Background: {request.Description.Trim()}");
        if (existing.Count > 0)
            lines.Add($"Already added (do not repeat): {string.Join(", ", existing)}");

        lines.Add(
            $"List up to {MaxSuggestions} keywords that ordinary people type into Google to find this kind of business - its different names, " +
            "the specialists and the places they work at (for an eye clinic: eye hospital, eye doctor, eye specialist, ophthalmologist, " +
            "optometrist, eye care centre, vision centre). Describe the businesses to find, not what we want to sell them. " +
            "Include a few of the common local-language names written in English letters, the way people type them " +
            "(for an Indian eye clinic, e.g. \"netra chikitsalaya\", \"aankh ka hospital\"). Each is 1 to 5 words, lowercase unless a proper noun, no punctuation.");
        return string.Join("\n", lines);
    }

    private static List<string> Clean(IEnumerable<string> keywords, ISet<string> existing) =>
        TenantBusinessDetails.NormalizeKeywords(keywords)
            .Where(k => k.Length <= LeadDiscoveryLimits.Keyword && !existing.Contains(k))
            .Take(MaxSuggestions)
            .ToList();
}
