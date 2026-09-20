using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

/// <summary>
/// Turns what a customer said into something the platform can compare, and rejects what does not fit
/// the field it was captured for.
///
/// Two rules shape all of it. First, the raw value is never discarded - an agent reading the lead
/// wants "around 1 cr", not "10000000". Second, a value that cannot be normalized confidently
/// normalizes to null rather than to a guess: a null just means filters and value-matching scoring
/// rules skip it, while a guessed date or amount is wrong data in the CRM that nobody knows to doubt.
/// </summary>
public static class QualificationValueNormalizer
{
    /// <summary>Indian-English shorthand for amounts, plus the plain ones. Ordered longest-first so
    /// "lakhs" is matched before "lakh" would strand a trailing 's'.</summary>
    private static readonly (string Suffix, decimal Multiplier)[] AmountSuffixes =
    {
        ("crores", 10_000_000m), ("crore", 10_000_000m), ("cr", 10_000_000m),
        ("lakhs", 100_000m), ("lakh", 100_000m), ("lacs", 100_000m), ("lac", 100_000m), ("l", 100_000m),
        ("million", 1_000_000m), ("mn", 1_000_000m), ("m", 1_000_000m),
        ("thousand", 1_000m), ("k", 1_000m)
    };

    private static readonly Regex AmountPattern = new(
        @"(\d+(?:[.,]\d+)?)\s*([a-z]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NonDigits = new(@"[^\d.\-]", RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(
        @"^[^@\s]+@[^@\s.]+\.[^@\s]+$", RegexOptions.Compiled);

    private static readonly Regex PhonePattern = new(
        @"^\+?[0-9][0-9 ()\-]{4,19}$", RegexOptions.Compiled);

    private static readonly string[] TruthyWords = { "yes", "y", "true", "haan", "ha", "ji", "sure", "ok", "okay" };
    private static readonly string[] FalsyWords = { "no", "n", "false", "nahi", "nahin", "nope" };

    /// <summary>Validates and normalizes one captured value against its field.</summary>
    public static NormalizationResult Normalize(QualificationField field, string rawValue)
    {
        var raw = (rawValue ?? string.Empty).Trim();
        if (raw.Length == 0)
            return NormalizationResult.Rejected("Value is empty.");

        if (raw.Length > 1000)
            raw = raw[..1000];

        // The pattern runs before type normalization: a tenant who constrained a field to a shape
        // meant that shape, whatever the type would otherwise have accepted.
        if (!string.IsNullOrWhiteSpace(field.ValidationPattern) && !MatchesPattern(field.ValidationPattern, raw))
            return NormalizationResult.Rejected("Value does not match the field's validation pattern.");

        return field.DataType switch
        {
            QualificationDataType.Text => NormalizationResult.Accepted(raw, raw),
            QualificationDataType.Number => NormalizeNumber(raw),
            QualificationDataType.Currency => NormalizeCurrency(raw),
            QualificationDataType.Date => NormalizeDate(raw),
            QualificationDataType.Boolean => NormalizeBoolean(raw),
            QualificationDataType.SingleChoice => NormalizeSingleChoice(field, raw),
            QualificationDataType.MultiChoice => NormalizeMultiChoice(field, raw),
            QualificationDataType.PhoneNumber => PhonePattern.IsMatch(raw)
                ? NormalizationResult.Accepted(raw, NonDigits.Replace(raw, string.Empty))
                : NormalizationResult.Rejected("Not a recognisable phone number."),
            QualificationDataType.Email => EmailPattern.IsMatch(raw)
                ? NormalizationResult.Accepted(raw, raw.ToLowerInvariant())
                : NormalizationResult.Rejected("Not a recognisable email address."),
            _ => NormalizationResult.Accepted(raw, null)
        };
    }

    public static IReadOnlyList<string> ParseAllowedValues(string? allowedValuesJson)
    {
        if (string.IsNullOrWhiteSpace(allowedValuesJson))
            return Array.Empty<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(allowedValuesJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            // Stored JSON that no longer parses is a configuration bug, not a reason to fail an
            // inbound message - an empty list simply means no choice constraint is enforced.
            return Array.Empty<string>();
        }
    }

    private static bool MatchesPattern(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            // A pattern that no longer compiles cannot reject anything meaningfully.
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            // Treated as a rejection: a pattern that cannot decide in 100ms on customer input is one
            // an attacker could use to stall the inbound path, and the value is not worth that.
            return false;
        }
    }

    private static NormalizationResult NormalizeNumber(string raw)
    {
        var cleaned = NonDigits.Replace(raw.Replace(",", string.Empty), string.Empty);
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? NormalizationResult.Accepted(raw, value.ToString(CultureInfo.InvariantCulture))
            : NormalizationResult.Accepted(raw, null);
    }

    /// <summary>Handles the shorthand customers actually type - "1 cr", "50k", "₹12,000", "2.5 lakh".
    /// A value with no recognisable number keeps its raw form and normalizes to null rather than
    /// being rejected: "depends on the offer" is a real answer to a budget question, just not a
    /// comparable one.</summary>
    private static NormalizationResult NormalizeCurrency(string raw)
    {
        var match = AmountPattern.Match(raw.Replace(",", string.Empty));
        if (!match.Success)
            return NormalizationResult.Accepted(raw, null);

        if (!decimal.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            return NormalizationResult.Accepted(raw, null);

        var suffix = match.Groups[2].Value.ToLowerInvariant();
        if (suffix.Length > 0)
        {
            var multiplier = AmountSuffixes.FirstOrDefault(s => s.Suffix == suffix).Multiplier;
            if (multiplier > 0)
                number *= multiplier;
        }

        return NormalizationResult.Accepted(raw, number.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Only parses an actual date. "Next month" and "after Diwali" deliberately normalize to
    /// null - converting them to a day would invent precision the customer never gave, and a scoring
    /// rule keyed on that date would then act on a number we made up.</summary>
    private static NormalizationResult NormalizeDate(string raw)
    {
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d MMM yyyy", "MMM d yyyy", "dd.MM.yyyy" };

        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return NormalizationResult.Accepted(raw, exact.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
            return NormalizationResult.Accepted(raw, loose.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return NormalizationResult.Accepted(raw, null);
    }

    private static NormalizationResult NormalizeBoolean(string raw)
    {
        var lower = raw.ToLowerInvariant();

        if (TruthyWords.Any(w => lower == w || lower.StartsWith(w + " ", StringComparison.Ordinal)))
            return NormalizationResult.Accepted(raw, "true");

        if (FalsyWords.Any(w => lower == w || lower.StartsWith(w + " ", StringComparison.Ordinal)))
            return NormalizationResult.Accepted(raw, "false");

        return NormalizationResult.Accepted(raw, null);
    }

    private static NormalizationResult NormalizeSingleChoice(QualificationField field, string raw)
    {
        var options = ParseAllowedValues(field.AllowedValuesJson);
        if (options.Count == 0)
            return NormalizationResult.Accepted(raw, raw);

        var match = options.FirstOrDefault(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(o => raw.Contains(o, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? NormalizationResult.Rejected($"Value must be one of: {string.Join(", ", options)}.")
            : NormalizationResult.Accepted(raw, match);
    }

    /// <summary>Normalizes to the matched options joined in the order the tenant declared them, not
    /// the order the customer said them, so two leads that picked the same options compare equal.</summary>
    private static NormalizationResult NormalizeMultiChoice(QualificationField field, string raw)
    {
        var options = ParseAllowedValues(field.AllowedValuesJson);
        if (options.Count == 0)
            return NormalizationResult.Accepted(raw, raw);

        var matched = options
            .Where(o => raw.Contains(o, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matched.Count == 0
            ? NormalizationResult.Rejected($"Value must include at least one of: {string.Join(", ", options)}.")
            : NormalizationResult.Accepted(raw, string.Join(";", matched));
    }
}

/// <summary>Accepted with an optional normalized form, or rejected with a reason. A rejection is not
/// an exception: a model returning something that does not fit a field is an ordinary outcome that
/// drops that one value and leaves the rest of the turn intact.</summary>
public record NormalizationResult(bool IsAccepted, string RawValue, string? NormalizedValue, string? RejectionReason)
{
    public static NormalizationResult Accepted(string raw, string? normalized) => new(true, raw, normalized, null);

    public static NormalizationResult Rejected(string reason) => new(false, string.Empty, null, reason);
}
