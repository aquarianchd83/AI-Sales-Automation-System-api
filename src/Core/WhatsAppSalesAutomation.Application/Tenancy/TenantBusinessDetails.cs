using FluentValidation;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>The optional business fields a tenant carries beyond its name - shared by the tenant's own
/// Business Profile save (<see cref="UpdateTenantBusinessProfileRequest"/>) and the Platform Admin
/// Console's tenant creation (Platform.CreatePlatformTenantRequest), so both paths validate and store
/// them identically.</summary>
public interface ITenantBusinessDetails
{
    string? ProductName { get; }
    string? Industry { get; }
    string? BusinessDescription { get; }
    string? WebsiteUrl { get; }
    string? SupportEmail { get; }
    string? SupportPhone { get; }
    IReadOnlyList<string>? DomainKeywords { get; }
}

/// <summary>Included by every validator whose request implements <see cref="ITenantBusinessDetails"/>.
/// Every field is optional; a blank value is valid and stored as null.</summary>
public class TenantBusinessDetailsValidator : AbstractValidator<ITenantBusinessDetails>
{
    public TenantBusinessDetailsValidator()
    {
        RuleFor(x => x.ProductName).MaximumLength(TenantProfileLimits.ProductName);
        RuleFor(x => x.Industry).MaximumLength(TenantProfileLimits.Industry);
        RuleFor(x => x.BusinessDescription).MaximumLength(TenantProfileLimits.BusinessDescription);

        RuleFor(x => x.WebsiteUrl)
            .MaximumLength(TenantProfileLimits.WebsiteUrl)
            .Must(BeAnHttpUrl)
            .WithMessage("Website must be a full address starting with http:// or https://.")
            .When(x => !string.IsNullOrWhiteSpace(x.WebsiteUrl));

        RuleFor(x => x.SupportEmail)
            .MaximumLength(TenantProfileLimits.SupportEmail)
            .EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.SupportEmail));

        RuleFor(x => x.SupportPhone)
            .MaximumLength(TenantProfileLimits.SupportPhone)
            .Matches(@"^\+?[0-9 ()\-]{5,31}$")
            .WithMessage("Support phone may contain only digits, spaces, parentheses, dashes and a leading +.")
            .When(x => !string.IsNullOrWhiteSpace(x.SupportPhone));

        RuleFor(x => x.DomainKeywords)
            .Must(keywords => keywords is null || keywords.Count <= TenantProfileLimits.MaxKeywords)
            .WithMessage($"Add at most {TenantProfileLimits.MaxKeywords} domain keywords.");

        RuleForEach(x => x.DomainKeywords)
            .MaximumLength(TenantProfileLimits.Keyword)
            .WithMessage($"Each domain keyword can be at most {TenantProfileLimits.Keyword} characters.");
    }

    private static bool BeAnHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public static class TenantBusinessDetails
{
    /// <summary>Copies every business field onto the tenant, trimming text, storing blanks as null, and
    /// normalizing the keyword list. Replaces all of them - a field left blank clears it.</summary>
    public static void ApplyTo(Tenant tenant, ITenantBusinessDetails details)
    {
        tenant.ProductName = Clean(details.ProductName);
        tenant.Industry = Clean(details.Industry);
        tenant.BusinessDescription = Clean(details.BusinessDescription);
        tenant.WebsiteUrl = Clean(details.WebsiteUrl);
        tenant.SupportEmail = Clean(details.SupportEmail);
        tenant.SupportPhone = Clean(details.SupportPhone);
        tenant.DomainKeywords = NormalizeKeywords(details.DomainKeywords);
    }

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Trims, collapses inner whitespace, drops blanks, and keeps the first spelling of each
    /// keyword that differs only by case.</summary>
    public static List<string> NormalizeKeywords(IEnumerable<string>? keywords) =>
        (keywords ?? Enumerable.Empty<string>())
            .Select(k => string.Join(' ', (k ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
