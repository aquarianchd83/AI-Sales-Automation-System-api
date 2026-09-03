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
    /// parameter list, ready for <c>IWhatsAppService.SendTemplateMessageAsync</c>. One parameter
    /// per DISTINCT token (same rule as <see cref="ExtractTokens"/>) - WhatsApp's approved template
    /// has one {{n}} per distinct variable, so a token reused later in the body (e.g. the same
    /// {{FirstName}} appearing twice) must resolve to the same positional parameter, not a second
    /// one, or Meta rejects the send with "(#132000) Number of parameters does not match".</summary>
    public static (string ResolvedText, IReadOnlyList<string> ParameterValues) Resolve(string bodyText, Customer customer)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = customer.FirstName ?? string.Empty,
            ["LastName"] = customer.LastName ?? string.Empty,
            ["PhoneNumber"] = customer.PhoneNumberE164
        };

        var tokenOrder = new List<string>();
        var resolvedText = TokenPattern.Replace(bodyText, match =>
        {
            var token = match.Groups[1].Value;
            if (!tokenOrder.Contains(token, StringComparer.OrdinalIgnoreCase))
                tokenOrder.Add(token);

            return values.TryGetValue(token, out var v) ? v : string.Empty;
        });

        var parameterValues = tokenOrder
            .Select(token => values.TryGetValue(token, out var v) ? v : string.Empty)
            .ToList();

        return (resolvedText, parameterValues);
    }

    /// <summary>Fixture values for each KnownTokens entry, used only as the "example" Meta's template
    /// creation/edit API mandates for every numbered placeholder (real customer data is never involved
    /// in registering a template - only in sending one, via Resolve above).</summary>
    private static readonly IReadOnlyDictionary<string, string> ExampleValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["FirstName"] = "John",
        ["LastName"] = "Doe",
        ["PhoneNumber"] = "+15551234567"
    };

    /// <summary>
    /// Converts our named-placeholder body ("Hi {{FirstName}}...") into Meta's own positional syntax
    /// ("Hi {{1}}...") plus the example values Meta requires for each position when creating or
    /// editing a template - the same first-occurrence token order ExtractTokens/Resolve already use,
    /// so the position assigned here is exactly what Resolve's ParameterValues will fill at send time.
    /// </summary>
    public static (string MetaBodyText, IReadOnlyList<string> ExampleValues) ToMetaTemplateBody(string bodyText)
    {
        var tokenOrder = ExtractTokens(bodyText);

        // A distinct token gets ONE position, reused for every occurrence - a repeated {{FirstName}}
        // must map to the same {{1}} both times, not a fresh number per regex match, or Resolve's
        // "one parameter per distinct token" contract (see its own doc comment) would no longer match
        // what was actually registered with Meta.
        var positionByToken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokenOrder.Count; i++)
            positionByToken[tokenOrder[i]] = i + 1;

        var metaBodyText = TokenPattern.Replace(bodyText, match => $"{{{{{positionByToken[match.Groups[1].Value]}}}}}");

        var examples = tokenOrder
            .Select(token => ExampleValues.TryGetValue(token, out var v) ? v : "value")
            .ToList();

        return (metaBodyText, examples);
    }
}
