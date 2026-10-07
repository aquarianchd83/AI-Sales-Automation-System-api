using System.Text.RegularExpressions;
using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>Body of the description refinement request: the text the tenant wrote, and the industry and speciality
/// for context.</summary>
public record RefineDescriptionRequest(string Description, string? Industry, string? IndustrySubcategory);

/// <summary>What came back. <see cref="Source"/> is "AI" when the tenant's AI provider rewrote it and "Tidied" when it
/// could only be cleaned up (spacing and punctuation) - the screen says which, so a tidy is never passed off as AI.</summary>
public record RefinedDescriptionDto(string Description, string Source);

public interface IDescriptionRefinementService
{
    Task<RefinedDescriptionDto> RefineAsync(RefineDescriptionRequest request, CancellationToken cancellationToken = default);
}

public class RefineDescriptionRequestValidator : AbstractValidator<RefineDescriptionRequest>
{
    public RefineDescriptionRequestValidator()
    {
        RuleFor(x => x.Description).NotEmpty().WithMessage("Write a first draft of the description to refine.")
            .MaximumLength(TenantProfileLimits.BusinessDescription);
        RuleFor(x => x.Industry).MaximumLength(TenantProfileLimits.Industry);
        RuleFor(x => x.IndustrySubcategory).MaximumLength(TenantProfileLimits.IndustrySubcategory);
    }
}

/// <summary>
/// Refines a business description the tenant has written: clearer, specific and in the tenant's own voice, never
/// longer than the field allows and never inventing facts. Asks the tenant's AI provider when one is really
/// configured; otherwise (Simulated, no key, or the call failed) it only tidies spacing and punctuation. Nothing is
/// saved - the screen shows the result and the tenant decides.
/// </summary>
public class DescriptionRefinementService : IDescriptionRefinementService
{
    private const string SystemPrompt =
        "You edit a short business description for a company profile. Improve clarity and flow, keep the owner's meaning " +
        "and facts, and do not invent products, prices, places or claims. Reply with ONLY the improved description - " +
        "plain text, no quotes, no headings, no commentary.";

    private readonly IAiTextGenerator _ai;
    private readonly IValidator<RefineDescriptionRequest> _validator;

    public DescriptionRefinementService(IAiTextGenerator ai, IValidator<RefineDescriptionRequest> validator)
    {
        _ai = ai;
        _validator = validator;
    }

    public async Task<RefinedDescriptionDto> RefineAsync(RefineDescriptionRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        var draft = Tidy(request.Description);
        var reply = await _ai.GenerateAsync(SystemPrompt, BuildPrompt(request, draft), 700, cancellationToken);
        var refined = Clean(reply);
        return string.IsNullOrWhiteSpace(refined)
            ? new RefinedDescriptionDto(draft, "Tidied")
            : new RefinedDescriptionDto(refined, "AI");
    }

    private static string BuildPrompt(RefineDescriptionRequest request, string draft)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Industry))
            lines.Add($"Industry: {request.Industry.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.IndustrySubcategory))
            lines.Add($"Speciality: {request.IndustrySubcategory.Trim()}");
        lines.Add($"Description to improve:\n{draft}");
        lines.Add($"Rewrite it in 2 to 4 sentences covering what the business offers, who it serves and what sets it apart - using only what the description says. At most {TenantProfileLimits.BusinessDescription} characters.");
        return string.Join("\n", lines);
    }

    /// <summary>The AI's reply as plain text that fits the field: code fences and wrapping quotes removed, trimmed, and cut
    /// at the limit on a sentence end when it runs over.</summary>
    internal static string Clean(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return string.Empty;

        var text = Regex.Replace(reply.Trim(), @"^```[a-z]*\s*|\s*```$", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();

        if (text.Length <= TenantProfileLimits.BusinessDescription)
            return text;

        var cut = text[..TenantProfileLimits.BusinessDescription];
        var lastStop = cut.LastIndexOfAny(new[] { '.', '!', '?' });
        return lastStop > 0 ? cut[..(lastStop + 1)] : cut.TrimEnd();
    }

    /// <summary>The fallback: collapse stray spaces, capitalise the first letter and end on a full stop.</summary>
    internal static string Tidy(string description)
    {
        var text = Regex.Replace(description.Trim(), @"[ \t]+", " ");
        text = Regex.Replace(text, @"\s+([,.;:!?])", "$1");
        text = Regex.Replace(text, @"([,;])(?=[A-Za-z])", "$1 "); // "a,b" -> "a, b"; "1,000" is left alone
        if (text.Length == 0)
            return text;

        text = char.ToUpperInvariant(text[0]) + text[1..];
        return text.Length < TenantProfileLimits.BusinessDescription && !".!?".Contains(text[^1]) ? text + "." : text;
    }
}
