using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>Body of the keyword suggestion request: the industry to suggest for, optionally the business description
/// for context, and the keywords already added (never suggested again).</summary>
public record SuggestKeywordsRequest(string Industry, string? BusinessDescription, IReadOnlyList<string>? Existing);

/// <summary>What came back. <see cref="Source"/> is "AI" when the tenant's AI provider wrote them, "Common terms" when
/// they came from the built-in list for well-known industries - the screen says which, so a lookup is never passed off
/// as AI.</summary>
public record KeywordSuggestionsDto(IReadOnlyList<string> Keywords, string Source);

public interface IKeywordSuggestionService
{
    Task<KeywordSuggestionsDto> SuggestAsync(SuggestKeywordsRequest request, CancellationToken cancellationToken = default);
}

public class SuggestKeywordsRequestValidator : AbstractValidator<SuggestKeywordsRequest>
{
    public SuggestKeywordsRequestValidator()
    {
        RuleFor(x => x.Industry).NotEmpty().WithMessage("Choose an industry first.").MaximumLength(TenantProfileLimits.Industry);
        RuleFor(x => x.BusinessDescription).MaximumLength(TenantProfileLimits.BusinessDescription);
        RuleFor(x => x.Existing).Must(e => e is null || e.Count <= TenantProfileLimits.MaxKeywords * 2);
    }
}

/// <summary>
/// Suggests domain keywords for the tenant's industry. Asks the tenant's AI provider when one is really configured;
/// otherwise (Simulated, no key, or the call failed) falls back to <see cref="IndustryKeywordCatalog"/>. Whatever the
/// source, the result is cleaned the way the profile cleans keywords - trimmed, short enough, no duplicates, nothing
/// the tenant already has - so every suggestion can be added as is.
/// </summary>
public class KeywordSuggestionService : IKeywordSuggestionService
{
    public const int MaxSuggestions = 12;

    private const string SystemPrompt =
        "You help a business describe what it deals in. Reply with ONLY a JSON array of strings - no prose, no code fences.";

    private readonly IAiTextGenerator _ai;
    private readonly IValidator<SuggestKeywordsRequest> _validator;

    public KeywordSuggestionService(IAiTextGenerator ai, IValidator<SuggestKeywordsRequest> validator)
    {
        _ai = ai;
        _validator = validator;
    }

    public async Task<KeywordSuggestionsDto> SuggestAsync(SuggestKeywordsRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var industry = request.Industry.Trim();
        var existing = new HashSet<string>(
            TenantBusinessDetails.NormalizeKeywords(request.Existing), StringComparer.OrdinalIgnoreCase);

        var reply = await _ai.GenerateAsync(SystemPrompt, BuildPrompt(industry, request.BusinessDescription, existing), 400, cancellationToken);
        var fromAi = Clean(ParseList(reply), existing);
        if (fromAi.Count > 0)
            return new KeywordSuggestionsDto(fromAi, "AI");

        return new KeywordSuggestionsDto(Clean(IndustryKeywordCatalog.For(industry), existing), "Common terms");
    }

    private static string BuildPrompt(string industry, string? description, IReadOnlyCollection<string> existing)
    {
        var lines = new List<string> { $"Industry: {industry}" };
        if (!string.IsNullOrWhiteSpace(description))
            lines.Add($"About the business: {description.Trim()}");
        if (existing.Count > 0)
            lines.Add($"Already added (do not repeat): {string.Join(", ", existing)}");

        lines.Add(
            $"Suggest up to {MaxSuggestions} keywords: the products, services and topics a business in this industry deals in, " +
            "as customers would search or ask for them. Each is 1 to 3 words, lowercase unless a proper noun, no punctuation.");
        return string.Join("\n", lines);
    }

    /// <summary>The first JSON array of strings in the reply, tolerating a code fence or a sentence around it.</summary>
    internal static IReadOnlyList<string> ParseList(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return Array.Empty<string>();

        var match = Regex.Match(reply, @"\[[\s\S]*?\]");
        if (!match.Success)
            return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty).ToList()
                : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static List<string> Clean(IEnumerable<string> keywords, ISet<string> existing) =>
        TenantBusinessDetails.NormalizeKeywords(keywords)
            .Where(k => k.Length <= TenantProfileLimits.Keyword && !existing.Contains(k))
            .Take(MaxSuggestions)
            .ToList();
}
