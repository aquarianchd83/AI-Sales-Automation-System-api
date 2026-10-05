using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Entities.Setup;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Setup;

/// <summary>The outcome of checking a set of answers against a version's requirements.</summary>
public sealed record SetupEvaluation(
    IReadOnlyList<string> VisibleKeys,
    IReadOnlyList<SetupFieldIssueDto> Missing,
    IReadOnlyList<SetupFieldIssueDto> Invalid,
    IReadOnlyList<SetupSectionProgressDto> Sections,
    int PercentComplete)
{
    public bool IsComplete => Missing.Count == 0 && Invalid.Count == 0;

    public SetupEvaluationDto ToDto() => new(IsComplete, PercentComplete, VisibleKeys, Missing, Invalid, Sections);
}

/// <summary>JSON read/write for the structured columns of a requirement.</summary>
public static class SetupJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<SetupOptionDto> ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<SetupOptionDto>();
        try
        {
            return JsonSerializer.Deserialize<List<SetupOptionDto>>(json, Options) ?? new List<SetupOptionDto>();
        }
        catch (JsonException)
        {
            return Array.Empty<SetupOptionDto>();
        }
    }

    public static SetupValidationDto? ParseValidation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SetupValidationDto>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? WriteOptions(IReadOnlyList<SetupOptionDto>? options) =>
        options is { Count: > 0 } ? JsonSerializer.Serialize(options, Options) : null;

    public static string? WriteValidation(SetupValidationDto? rules) =>
        rules is null || (rules.Min is null && rules.Max is null && rules.MinLength is null && rules.MaxLength is null
                          && string.IsNullOrWhiteSpace(rules.Pattern))
            ? null
            : JsonSerializer.Serialize(rules, Options);

    public static string WriteList(IEnumerable<string> values) => JsonSerializer.Serialize(values, Options);

    /// <summary>The picks of a multi-select answer; a plain answer is a list of one.</summary>
    public static IReadOnlyList<string> Tokens(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return Array.Empty<string>();
        var trimmed = answer.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(trimmed, Options)?
                    .Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            }
            catch (JsonException)
            {
                // Not a list after all - fall through and treat it as plain text.
            }
        }

        return new[] { trimmed };
    }
}

/// <summary>
/// Turns one raw JSON answer into the canonical string it is stored as, and back into a typed value for the API.
/// </summary>
public static class SetupValueCodec
{
    public const int MaxStoredLength = 4000;

    /// <summary>Returns the canonical string (null when the answer is blank) or an error for a shape that field can never hold.</summary>
    public static (string? Value, string? Error) Normalize(JsonElement element, SetupFieldType type)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return (null, null);

            case JsonValueKind.String:
                var text = element.GetString()?.Trim();
                if (string.IsNullOrEmpty(text))
                    return (null, null);
                // A multi-select sent as one string is read as a one-item list; everything else stays text.
                return (type == SetupFieldType.MultiSelect && !text.StartsWith('[') ? SetupJson.WriteList(new[] { text }) : text, null);

            case JsonValueKind.Number:
                return (element.GetRawText(), null);

            case JsonValueKind.True:
            case JsonValueKind.False:
                return (element.GetBoolean() ? "true" : "false", null);

            case JsonValueKind.Array:
                var items = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return (null, "Choose from the listed options.");
                    var value = item.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(value) && !items.Contains(value, StringComparer.OrdinalIgnoreCase))
                        items.Add(value);
                }

                return (items.Count == 0 ? null : SetupJson.WriteList(items), null);

            default:
                return (null, "That value is not valid for this field.");
        }
    }

    /// <summary>The stored string as the type the UI binds to: a list for multi-select, a bool for a checkbox,
    /// a number for the numeric types, otherwise the text.</summary>
    public static object? ToTyped(string? stored, SetupFieldType type)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return type == SetupFieldType.MultiSelect ? Array.Empty<string>() : null;

        switch (type)
        {
            case SetupFieldType.MultiSelect:
                return SetupJson.Tokens(stored);
            case SetupFieldType.Checkbox:
                return string.Equals(stored, "true", StringComparison.OrdinalIgnoreCase);
            case SetupFieldType.Number:
            case SetupFieldType.Decimal:
            case SetupFieldType.Currency:
                return decimal.TryParse(stored, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : stored;
            default:
                return stored;
        }
    }
}

/// <summary>
/// The single place that decides, for a version's requirements and a set of answers: which fields apply, what is
/// missing, what is wrong, and how far along the setup is. Pure - no database, no clock - so the same rules
/// run when a draft is saved, when setup is completed, and again immediately before an application executes.
/// The Talent UI mirrors the visibility and validation rules for instant feedback, but this is the authority.
/// </summary>
public static class SetupEvaluator
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private const decimal MaxMoney = 1_000_000_000m;

    /// <param name="requirements">The ACTIVE requirements of the application's pinned version.</param>
    /// <param name="values">Canonical answers by field key (defaults already applied).</param>
    public static SetupEvaluation Evaluate(IReadOnlyCollection<PlanRequirement> requirements, IReadOnlyDictionary<string, string?> values)
    {
        var ordered = requirements.Where(r => r.IsActive).OrderBy(r => r.DisplayOrder).ThenBy(r => r.Label).ToList();
        var byKey = ordered.ToDictionary(r => r.FieldKey, StringComparer.OrdinalIgnoreCase);
        var memo = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var visible = new List<PlanRequirement>();
        foreach (var requirement in ordered)
        {
            if (IsVisible(requirement, byKey, values, memo, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                visible.Add(requirement);
        }

        var missing = new List<SetupFieldIssueDto>();
        var invalid = new List<SetupFieldIssueDto>();
        var answeredRequired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var requiredCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in visible)
        {
            values.TryGetValue(requirement.FieldKey, out var answer);
            var blank = IsBlank(requirement.FieldType, answer);

            if (!blank)
                started.Add(requirement.Section);

            if (requirement.IsRequired)
                requiredCount[requirement.Section] = requiredCount.GetValueOrDefault(requirement.Section) + 1;

            if (blank)
            {
                if (requirement.IsRequired)
                    missing.Add(new SetupFieldIssueDto(requirement.FieldKey, RequiredMessage(requirement)));
                continue;
            }

            var problem = ValidateValue(requirement, answer);
            if (problem is not null)
                invalid.Add(new SetupFieldIssueDto(requirement.FieldKey, problem));
            else if (requirement.IsRequired)
                answeredRequired[requirement.Section] = answeredRequired.GetValueOrDefault(requirement.Section) + 1;
        }

        var sections = visible
            .Select(r => r.Section)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                var info = SetupSections.Describe(key);
                var required = requiredCount.GetValueOrDefault(key);
                var done = answeredRequired.GetValueOrDefault(key);
                var hasIssue = missing.Concat(invalid).Any(i => visible.First(v => v.FieldKey == i.FieldKey).Section.Equals(key, StringComparison.OrdinalIgnoreCase));
                return (Order: info.Order, Progress: new SetupSectionProgressDto(key, info.Title, required, done, started.Contains(key), !hasIssue));
            })
            .OrderBy(s => s.Order)
            .Select(s => s.Progress)
            .ToList();

        var totalRequired = requiredCount.Values.Sum();
        var totalDone = answeredRequired.Values.Sum();
        var percent = totalRequired == 0 ? 100 : (int)Math.Floor(100.0 * totalDone / totalRequired);

        return new SetupEvaluation(visible.Select(v => v.FieldKey).ToList(), missing, invalid, sections, percent);
    }

    /// <summary>Whether a requirement applies given the answers: no condition, or its parent is itself visible and the
    /// parent's answer matches. An unknown parent, or a cycle, hides the field - the safe direction, since a hidden
    /// field is never required.</summary>
    public static bool IsVisible(
        PlanRequirement requirement,
        IReadOnlyDictionary<string, PlanRequirement> byKey,
        IReadOnlyDictionary<string, string?> values,
        IDictionary<string, bool> memo,
        ISet<string> visiting)
    {
        if (memo.TryGetValue(requirement.FieldKey, out var known))
            return known;

        bool result;
        if (string.IsNullOrWhiteSpace(requirement.ConditionFieldKey) || requirement.ConditionOperator is null)
        {
            result = true;
        }
        else if (!visiting.Add(requirement.FieldKey) || !byKey.TryGetValue(requirement.ConditionFieldKey, out var parent))
        {
            result = false;
        }
        else
        {
            values.TryGetValue(parent.FieldKey, out var parentAnswer);
            result = IsVisible(parent, byKey, values, memo, visiting)
                     && ConditionMatches(requirement.ConditionOperator.Value, parentAnswer, requirement.ConditionValue);
        }

        memo[requirement.FieldKey] = result;
        return result;
    }

    public static bool ConditionMatches(SetupConditionOperator op, string? answer, string? expected)
    {
        var tokens = SetupJson.Tokens(answer);
        var wanted = (expected ?? string.Empty).Trim();

        switch (op)
        {
            case SetupConditionOperator.Empty:
                return tokens.Count == 0;
            case SetupConditionOperator.NotEmpty:
                return tokens.Count > 0;
            case SetupConditionOperator.Equals:
                return tokens.Any(t => t.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            case SetupConditionOperator.NotEquals:
                return !tokens.Any(t => t.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            case SetupConditionOperator.Contains:
                return tokens.Any(t => t.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                       || (tokens.Count == 1 && wanted.Length > 0 && tokens[0].Contains(wanted, StringComparison.OrdinalIgnoreCase));
            case SetupConditionOperator.In:
                var set = wanted.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return tokens.Any(t => set.Contains(t, StringComparer.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    /// <summary>A required checkbox must be ticked, so an unticked one counts as no answer. Anything that is neither
    /// "true" nor "false" is not blank - it is a wrong answer, and <see cref="ValidateValue"/> says so.</summary>
    public static bool IsBlank(SetupFieldType type, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return true;
        return type switch
        {
            SetupFieldType.MultiSelect => SetupJson.Tokens(answer).Count == 0,
            SetupFieldType.Checkbox => string.Equals(answer.Trim(), "false", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static string RequiredMessage(PlanRequirement requirement) => requirement.FieldType switch
    {
        SetupFieldType.Checkbox => $"Please confirm: {requirement.Label}.",
        SetupFieldType.MultiSelect => $"Select at least one option for {requirement.Label}.",
        SetupFieldType.Dropdown or SetupFieldType.Radio => $"Choose an option for {requirement.Label}.",
        SetupFieldType.FileUpload => $"Upload a file for {requirement.Label}.",
        _ => $"{requirement.Label} is required."
    };

    /// <summary>Format and range check of a NON-blank answer. Null = acceptable.</summary>
    public static string? ValidateValue(PlanRequirement requirement, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;

        var rules = SetupJson.ParseValidation(requirement.ValidationJson);
        var label = requirement.Label;
        var text = answer.Trim();

        switch (requirement.FieldType)
        {
            case SetupFieldType.Text:
            case SetupFieldType.MultilineText:
            {
                var max = rules?.MaxLength ?? (requirement.FieldType == SetupFieldType.Text ? 255 : SetupValueCodec.MaxStoredLength);
                if (text.Length > max)
                    return $"{label} must be at most {max} characters.";
                if (rules?.MinLength is { } min && text.Length < min)
                    return $"{label} must be at least {min} characters.";
                return PatternProblem(rules, text, label);
            }

            case SetupFieldType.Number:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                    return $"{label} must be a whole number.";
                return RangeProblem(rules, whole, label);

            case SetupFieldType.Decimal:
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                    return $"{label} must be a number.";
                return RangeProblem(rules, number, label);

            case SetupFieldType.Currency:
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var money))
                    return $"{label} must be an amount.";
                if (money < 0)
                    return $"{label} cannot be negative.";
                if (money > MaxMoney)
                    return $"{label} is too large.";
                return RangeProblem(rules, money, label);

            case SetupFieldType.Date:
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? null
                    : $"{label} must be a valid date.";

            case SetupFieldType.Dropdown:
            case SetupFieldType.Radio:
                return OptionProblem(requirement, new[] { text });

            case SetupFieldType.MultiSelect:
            {
                var picks = SetupJson.Tokens(answer);
                var problem = OptionProblem(requirement, picks);
                if (problem is not null)
                    return problem;
                if (rules?.Min is { } minPicks && picks.Count < minPicks)
                    return $"Select at least {minPicks:0} options for {label}.";
                if (rules?.Max is { } maxPicks && picks.Count > maxPicks)
                    return $"Select at most {maxPicks:0} options for {label}.";
                return null;
            }

            case SetupFieldType.Checkbox:
                return text is "true" or "false" ? null : $"{label} must be ticked or not.";

            case SetupFieldType.FileUpload:
                return text.Length > 500 ? $"{label} reference is too long." : null;

            case SetupFieldType.Url:
                return text.Length <= 2000 && Uri.TryCreate(text, UriKind.Absolute, out var uri)
                       && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host)
                    ? null
                    : $"{label} must be a valid web address starting with http:// or https://.";

            case SetupFieldType.Email:
                return text.Length <= 254 && EmailPattern.IsMatch(text) ? null : $"{label} must be a valid email address.";

            case SetupFieldType.Phone:
            {
                var digits = text.Where(char.IsDigit).Count();
                var onlyPhoneChars = text.All(c => char.IsDigit(c) || c is '+' or ' ' or '-' or '(' or ')' or '.');
                var plusOk = !text.Contains('+') || text.LastIndexOf('+') == 0;
                return onlyPhoneChars && plusOk && digits is >= 7 and <= 15 ? null : $"{label} must be a valid phone number.";
            }

            default:
                return null;
        }
    }

    private static string? RangeProblem(SetupValidationDto? rules, decimal value, string label)
    {
        if (rules?.Min is { } min && value < min)
            return $"{label} must be at least {min:0.##}.";
        if (rules?.Max is { } max && value > max)
            return $"{label} must be at most {max:0.##}.";
        return null;
    }

    private static string? PatternProblem(SetupValidationDto? rules, string text, string label)
    {
        if (string.IsNullOrWhiteSpace(rules?.Pattern))
            return null;
        try
        {
            return Regex.IsMatch(text, rules.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250))
                ? null
                : string.IsNullOrWhiteSpace(rules.PatternMessage) ? $"{label} is not in the expected format." : rules.PatternMessage;
        }
        catch (ArgumentException)
        {
            return null; // An admin typed a broken pattern; never lock a Talent out because of it.
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string? OptionProblem(PlanRequirement requirement, IReadOnlyList<string> picks)
    {
        var options = SetupJson.ParseOptions(requirement.OptionsJson);
        if (options.Count == 0)
            return null;
        return picks.All(p => options.Any(o => o.Value.Equals(p, StringComparison.OrdinalIgnoreCase)))
            ? null
            : $"Choose from the listed options for {requirement.Label}.";
    }
}

/// <summary>Customers x price = revenue; minus the tagged costs = profit; profit / cost = ROI.</summary>
public static class SetupProjectionCalculator
{
    public static ApplicationProjectionDto Calculate(IEnumerable<PlanRequirement> visible, IReadOnlyDictionary<string, string?> values)
    {
        decimal? Read(string metric)
        {
            var total = 0m;
            var any = false;
            foreach (var requirement in visible.Where(r => string.Equals(r.MetricKey, metric, StringComparison.Ordinal)))
            {
                if (values.TryGetValue(requirement.FieldKey, out var raw)
                    && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                {
                    total += parsed;
                    any = true;
                }
            }

            return any ? total : null;
        }

        var price = Read(SetupMetrics.PackagePrice);
        var customers = Read(SetupMetrics.ExpectedCustomers);
        var leads = Read(SetupMetrics.ExpectedLeads);
        var marketing = Read(SetupMetrics.MarketingCost) ?? 0m;
        var social = Read(SetupMetrics.SocialMediaCost) ?? 0m;
        var operational = Read(SetupMetrics.OperationalCost) ?? 0m;
        var totalCost = marketing + social + operational;

        decimal? revenue = price is { } p && customers is { } c ? p * c : null;
        decimal? profit = revenue is { } r ? r - totalCost : null;
        decimal? roi = profit is { } pr && totalCost > 0 ? Math.Round(pr / totalCost * 100m, 1) : null;

        var tagged = visible.Select(v => v.MetricKey).Where(m => m is not null).ToHashSet();
        var missing = new List<string>();
        if (tagged.Contains(SetupMetrics.PackagePrice) && price is null) missing.Add("Package price");
        if (tagged.Contains(SetupMetrics.ExpectedCustomers) && customers is null) missing.Add("Expected customers");

        return new ApplicationProjectionDto(
            revenue is not null || totalCost > 0, price, customers, leads, revenue,
            marketing, social, operational, totalCost, profit, roi, missing);
    }
}
