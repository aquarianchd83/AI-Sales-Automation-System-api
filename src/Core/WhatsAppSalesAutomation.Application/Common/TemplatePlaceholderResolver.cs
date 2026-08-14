using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Application.Common;

/// <summary>
/// Resolves <c>{{Token}}</c> placeholders in a template/step body against a customer. Kept to a
/// small known set rather than arbitrary property reflection, so a typo in a template ("{{Frist
/// Name}}") is caught as a validation error when the template is saved, not as a broken send later.
/// </summary>
public static class TemplatePlaceholderResolver
{
    private static readonly Regex TokenPattern = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static readonly IReadOnlyList<string> KnownTokens = new[] { "FirstName", "LastName", "PhoneNumber" };

    /// <summary>Distinct token names, in first-occurrence order - this order is what WhatsApp's
    /// positional {{1}}, {{2}}... parameters must match at send time.</summary>
    public static IReadOnlyList<string> ExtractTokens(string bodyText)
    {
        var tokens = new List<string>();
        foreach (Match match in TokenPattern.Matches(bodyText))
        {
            var token = match.Groups[1].Value;
            if (!tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                tokens.Add(token);
        }

        return tokens;
    }

    public static bool TryValidateTokens(string bodyText, out IReadOnlyList<string> unknownTokens)
    {
        unknownTokens = ExtractTokens(bodyText)
            .Where(t => !KnownTokens.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return unknownTokens.Count == 0;
    }

    /// <summary>Substitutes every token with the customer's value and returns the positional
    /// parameter list in the same order, ready for <c>IWhatsAppService.SendTemplateMessageAsync</c>.</summary>
    public static (string ResolvedText, IReadOnlyList<string> ParameterValues) Resolve(string bodyText, Customer customer)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = customer.FirstName ?? string.Empty,
            ["LastName"] = customer.LastName ?? string.Empty,
            ["PhoneNumber"] = customer.PhoneNumberE164
        };

        var parameterValues = new List<string>();
        var resolvedText = TokenPattern.Replace(bodyText, match =>
        {
            var token = match.Groups[1].Value;
            var value = values.TryGetValue(token, out var v) ? v : string.Empty;
            parameterValues.Add(value);
            return value;
        });

        return (resolvedText, parameterValues);
    }
}
