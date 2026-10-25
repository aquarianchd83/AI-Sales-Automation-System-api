using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// What a WhatsApp Consent Page must contain before a customer may be asked to agree, and the rules for the text. Enforced on the
/// server for every save and publish - the UI repeats them for convenience only.
/// </summary>
public static class ConsentPageRules
{
    public const int MaxBusinessName = 120;
    public const int MinDescription = 10;
    public const int MaxDescription = 1000;
    public const int MaxAdditionalInfo = 4000;
    public const int MaxStatement = 500;
    public const int MaxUrl = 500;

    /// <summary>The statement with [Business Name] and [Product/Service] filled in is stored as evidence, so it has a ceiling of its own.</summary>
    public const int MaxResolvedStatement = 1000;

    public const int MaxProductLabel = 200;

    public const string BusinessNameToken = "[Business Name]";
    public const string ProductToken = "[Product/Service]";

    /// <summary>What [Product/Service] reads as when the tenant has not named a product.</summary>
    public const string GenericProduct = "its products and services";

    public const string DefaultStatementTemplate =
        "I agree to receive WhatsApp messages from [Business Name] about [Product/Service], including relevant follow-ups. I understand that I can opt out at any time.";

    // Content is plain text, shown as text. Anything that looks like markup is refused rather than "sanitised", so it can never be rendered.
    private static readonly Regex Markup = new(@"<\s*/?\s*[a-zA-Z!]", RegexOptions.Compiled);

    public static string ResolveStatement(string? statement, string businessName, string? productLabel)
    {
        var text = string.IsNullOrWhiteSpace(statement) ? DefaultStatementTemplate : statement.Trim();
        return text
            .Replace(BusinessNameToken, businessName, StringComparison.OrdinalIgnoreCase)
            .Replace(ProductToken, string.IsNullOrWhiteSpace(productLabel) ? GenericProduct : productLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Problems that make a value unacceptable even in a draft (too long, markup, a bad URL). A draft may be saved while incomplete, but never with
    /// these.
    /// </summary>
    public static List<ConsentPageIssueDto> ValidateFormat(SaveConsentPageSettingsRequest r)
    {
        var issues = new List<ConsentPageIssueDto>();
        Check(issues, "businessName", r.BusinessName, MaxBusinessName, singleLine: true);
        Check(issues, "productDescription", r.ProductDescription, MaxDescription);
        Check(issues, "additionalInfo", r.AdditionalInfo, MaxAdditionalInfo);
        Check(issues, "consentStatement", r.ConsentStatement, MaxStatement);
        CheckUrl(issues, "privacyPolicyUrl", r.PrivacyPolicyUrl);
        CheckUrl(issues, "termsUrl", r.TermsUrl);
        return issues;
    }

    /// <summary>Everything that must be true before the page may be published (and so before a customer is asked).</summary>
    public static List<ConsentPageIssueDto> ValidateForPublish(
        string? businessName, string? description, string? additionalInfo, string? statement, string? privacyUrl, string? termsUrl)
    {
        var issues = ValidateFormat(new SaveConsentPageSettingsRequest(businessName, description, additionalInfo, statement, privacyUrl, termsUrl));

        if (string.IsNullOrWhiteSpace(businessName))
            issues.Add(new("businessName", "Enter the business or product name."));

        if (string.IsNullOrWhiteSpace(description))
            issues.Add(new("productDescription", "Describe the product or service in a sentence or two."));
        else if (description.Trim().Length < MinDescription)
            issues.Add(new("productDescription", $"The description must be at least {MinDescription} characters."));

        // The customer is agreeing to WhatsApp messages; wording that never says so would not be consent to them.
        var resolved = ResolveStatement(statement, businessName?.Trim() ?? string.Empty, null);
        if (!resolved.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("consentStatement", "The consent statement must say that the customer agrees to receive WhatsApp messages."));
        else if (resolved.Length > MaxResolvedStatement)
            issues.Add(new("consentStatement", "The consent statement is too long once the business name is filled in. Shorten it."));

        return issues;
    }

    /// <summary>The content the customer is shown for a published snapshot.</summary>
    public static ConsentPageContentDto ToContent(ConsentPageVersion v) =>
        new(v.BusinessName, v.ProductDescription, v.AdditionalInfo, v.ConsentStatement, v.PrivacyPolicyUrl, v.TermsUrl, v.Version);

    private static void Check(List<ConsentPageIssueDto> issues, string field, string? value, int max, bool singleLine = false)
    {
        if (string.IsNullOrEmpty(value))
            return;
        if (value.Length > max)
            issues.Add(new(field, $"Keep this to {max} characters or fewer."));
        if (Markup.IsMatch(value))
            issues.Add(new(field, "Use plain text only - HTML or markup is not allowed."));
        if (singleLine && value.Any(char.IsControl))
            issues.Add(new(field, "This must be a single line."));
    }

    private static void CheckUrl(List<ConsentPageIssueDto> issues, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var url = value.Trim();
        if (url.Length > MaxUrl)
            issues.Add(new(field, $"Keep the link to {MaxUrl} characters or fewer."));
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                 || string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.') || !string.IsNullOrEmpty(uri.UserInfo) || url.Any(char.IsWhiteSpace))
            issues.Add(new(field, "Enter a full https:// link, for example https://example.com/privacy."));
    }
}
