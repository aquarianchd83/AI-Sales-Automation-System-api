using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>
/// The rules of a questionnaire, with no database and no HTTP in them: which questions are asked given the answers so far, whether an
/// answer is acceptable, what a definition must look like, and what facts a set of answers produces. Everything that decides "is this
/// question visible / is this answer valid" lives here once, so the tenant wizard, the platform preview and the save endpoint cannot
/// disagree.
/// </summary>
public static class QuestionnaireEngine
{
    public const int MaxQuestions = 100;
    public const int DefaultShortTextLength = 200;
    public const int DefaultLongTextLength = 4000;

    // Limits that together guarantee a generated fact always fits its column (see MaxFactStatementLength): the longest answer is a
    // 4000-character text, or a structured/multiple-choice answer built from these bounded pieces, plus a template of at most 300.
    public const int MaxTextLength = DefaultLongTextLength;
    public const int MaxOptions = 40;
    public const int MaxOptionLabel = 100;
    public const int MaxFields = 12;
    public const int MaxFieldLabel = 60;
    public const int MaxFactTemplate = 300;
    public const int MaxFactCategory = 60;

    /// <summary>The longest statement a fact can have. The database column must be at least this wide.</summary>
    public const int MaxFactStatementLength = 4500;

    /// <summary>The business-profile fields a text question may start from.</summary>
    public static readonly IReadOnlyList<string> PrefillSources = new[]
    {
        "Name", "ProductName", "Industry", "IndustrySubcategory", "BusinessDescription", "WebsiteUrl", "SupportEmail",
        "SupportPhone", "BusinessLocation", "WorkingHours", "TargetAudience", "TargetLocation"
    };

    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9_]{0,59}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // ----- reading and checking a definition -------------------------------------------------------------------------------------------

    /// <summary>Reads a stored definition and checks it. Throws <see cref="QuestionnaireDefinitionException"/> listing every problem.</summary>
    public static QuestionnaireDefinition Parse(string json)
    {
        QuestionnaireDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<QuestionnaireDefinition>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new QuestionnaireDefinitionException(new[] { new QuestionnaireIssue("definition", $"Not valid JSON for a questionnaire: {ex.Message}") });
        }

        if (definition is null)
            throw new QuestionnaireDefinitionException(new[] { new QuestionnaireIssue("definition", "The definition is empty.") });

        var issues = Check(definition);
        if (issues.Count > 0)
            throw new QuestionnaireDefinitionException(issues);

        return definition;
    }

    /// <summary>Null when the text is a readable questionnaire definition (it may still be incomplete); otherwise what is wrong with it.
    /// What a draft is held to - semantic problems are only refused at publish.</summary>
    public static string? ReadProblem(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<QuestionnaireDefinition>(json, JsonOptions) is null ? "The definition is empty." : null;
        }
        catch (JsonException ex)
        {
            return $"Not valid JSON for a questionnaire: {ex.Message}";
        }
    }

    /// <summary>Reads a definition without throwing, returning what was wrong instead.</summary>
    public static (QuestionnaireDefinition? Definition, IReadOnlyList<QuestionnaireIssue> Issues) TryParse(string json)
    {
        try
        {
            return (Parse(json), Array.Empty<QuestionnaireIssue>());
        }
        catch (QuestionnaireDefinitionException ex)
        {
            return (null, ex.Issues);
        }
    }

    public static string Serialize(QuestionnaireDefinition definition) => JsonSerializer.Serialize(definition, JsonOptions);

    public static IReadOnlyList<QuestionnaireIssue> Check(QuestionnaireDefinition definition)
    {
        var issues = new List<QuestionnaireIssue>();

        if (definition.Questions.Count == 0)
            issues.Add(new("definition", "Add at least one question."));
        if (definition.Questions.Count > MaxQuestions)
            issues.Add(new("definition", $"A questionnaire can have at most {MaxQuestions} questions."));

        var earlier = new Dictionary<string, QuestionDef>(StringComparer.Ordinal);
        foreach (var q in definition.Questions)
        {
            var key = string.IsNullOrWhiteSpace(q.Key) ? "(no key)" : q.Key;

            if (!KeyPattern.IsMatch(q.Key ?? string.Empty))
                issues.Add(new(key, "The key must start with a lower-case letter and use only lower-case letters, digits and underscores (60 characters at most)."));
            else if (earlier.ContainsKey(q.Key))
                issues.Add(new(key, "This key is used by more than one question."));

            if (string.IsNullOrWhiteSpace(q.Prompt))
                issues.Add(new(key, "The question needs a prompt."));
            else if (q.Prompt.Length > 300)
                issues.Add(new(key, "The prompt is longer than 300 characters."));

            CheckShape(q, key, issues);

            if (q.ShowWhen is not null)
                CheckCondition(q.ShowWhen, key, earlier, issues);

            if (q.PrefillFrom is not null)
            {
                if (!PrefillSources.Contains(q.PrefillFrom, StringComparer.Ordinal))
                    issues.Add(new(key, $"PrefillFrom must be one of: {string.Join(", ", PrefillSources)}."));
                else if (q.Type is not (QuestionType.ShortText or QuestionType.LongText))
                    issues.Add(new(key, "Only a text question can start from the business profile."));
            }

            if (q.MaxLength is { } maxLength && (maxLength < 1 || maxLength > MaxTextLength))
                issues.Add(new(key, $"MaxLength must be between 1 and {MaxTextLength}."));

            if (q.Fact is not null)
            {
                if (string.IsNullOrWhiteSpace(q.Fact.Category))
                    issues.Add(new(key, "A fact rule needs a category."));
                else if (q.Fact.Category.Trim().Length > MaxFactCategory)
                    issues.Add(new(key, $"A fact rule's category can be at most {MaxFactCategory} characters."));
                if (string.IsNullOrWhiteSpace(q.Fact.Template) || !q.Fact.Template.Contains("{value}", StringComparison.Ordinal))
                    issues.Add(new(key, "A fact rule's template must contain {value}, where the answer goes."));
                if (q.Fact.Template?.Length > MaxFactTemplate)
                    issues.Add(new(key, $"A fact rule's template can be at most {MaxFactTemplate} characters."));
            }

            if (KeyPattern.IsMatch(q.Key ?? string.Empty))
                earlier.TryAdd(q.Key!, q);
        }

        return issues;
    }

    private static void CheckShape(QuestionDef q, string key, List<QuestionnaireIssue> issues)
    {
        switch (q.Type)
        {
            case QuestionType.SingleChoice:
            case QuestionType.MultiChoice:
                if (q.Options.Count == 0)
                    issues.Add(new(key, "A choice question needs at least one option."));
                if (q.Options.Any(o => string.IsNullOrWhiteSpace(o.Value) || string.IsNullOrWhiteSpace(o.Label)))
                    issues.Add(new(key, "Every option needs a value and a label."));
                if (q.Options.Select(o => o.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != q.Options.Count)
                    issues.Add(new(key, "Two options share the same value."));
                if (q.Options.Count > MaxOptions)
                    issues.Add(new(key, $"A choice question can have at most {MaxOptions} options."));
                if (q.Options.Any(o => o.Label?.Length > MaxOptionLabel))
                    issues.Add(new(key, $"An option label can be at most {MaxOptionLabel} characters."));
                break;

            case QuestionType.Structured:
                if (q.Fields.Count == 0)
                    issues.Add(new(key, "A structured question needs at least one field."));
                if (q.Fields.Any(f => !KeyPattern.IsMatch(f.Key ?? string.Empty)))
                    issues.Add(new(key, "Every field needs a key of lower-case letters, digits and underscores."));
                if (q.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != q.Fields.Count)
                    issues.Add(new(key, "Two fields share the same key."));
                if (q.Fields.Count > MaxFields)
                    issues.Add(new(key, $"A structured question can have at most {MaxFields} fields."));
                if (q.Fields.Any(f => f.Label?.Length > MaxFieldLabel))
                    issues.Add(new(key, $"A field label can be at most {MaxFieldLabel} characters."));
                if (q.Fields.Any(f => string.IsNullOrWhiteSpace(f.Label)))
                    issues.Add(new(key, "Every field needs a label."));
                if (q.Fields.Any(f => f.Type is QuestionType.SingleChoice or QuestionType.MultiChoice or QuestionType.Structured))
                    issues.Add(new(key, "A field can only be text, a number or yes/no."));
                break;
        }

        if (q.Type is not (QuestionType.SingleChoice or QuestionType.MultiChoice) && q.Options.Count > 0)
            issues.Add(new(key, "Only a choice question has options."));
        if (q.Type != QuestionType.Structured && q.Fields.Count > 0)
            issues.Add(new(key, "Only a structured question has fields."));
        if (q.Type == QuestionType.Number && q.Min is { } min && q.Max is { } max && min > max)
            issues.Add(new(key, "Min is greater than Max."));
    }

    private static void CheckCondition(ConditionDef c, string key, IReadOnlyDictionary<string, QuestionDef> earlier, List<QuestionnaireIssue> issues)
    {
        var groups = (c.All is not null ? 1 : 0) + (c.Any is not null ? 1 : 0);
        var isLeaf = c.QuestionKey is not null || c.Operator is not null;

        if (groups + (isLeaf ? 1 : 0) != 1)
        {
            issues.Add(new(key, "A condition is either one comparison (QuestionKey, Operator, Value) or a list under All or Any."));
            return;
        }

        if (c.All is not null || c.Any is not null)
        {
            var children = c.All ?? c.Any!;
            if (children.Count == 0)
                issues.Add(new(key, "An All or Any list needs at least one condition."));
            foreach (var child in children)
                CheckCondition(child, key, earlier, issues);
            return;
        }

        if (string.IsNullOrWhiteSpace(c.QuestionKey) || c.Operator is null)
        {
            issues.Add(new(key, "A comparison needs both a QuestionKey and an Operator."));
            return;
        }

        // Only an earlier question can decide whether this one is asked, which also rules out a question depending on itself or in a loop.
        if (!earlier.TryGetValue(c.QuestionKey, out var target))
        {
            issues.Add(new(key, $"The condition refers to '{c.QuestionKey}', which is not an earlier question."));
            return;
        }

        var needsValue = c.Operator is not (ConditionOperator.Answered or ConditionOperator.NotAnswered);
        if (needsValue && (c.Value is null || c.Value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            issues.Add(new(key, $"The '{c.Operator}' comparison on '{c.QuestionKey}' needs a Value."));
        if (c.Operator == ConditionOperator.In && c.Value is { ValueKind: not JsonValueKind.Array })
            issues.Add(new(key, "'In' needs a list of values."));
        if (c.Operator is ConditionOperator.GreaterThan or ConditionOperator.LessThan && target.Type != QuestionType.Number)
            issues.Add(new(key, $"'{c.Operator}' only works on a number question, and '{c.QuestionKey}' is not one."));
    }

    // ----- which questions are asked ---------------------------------------------------------------------------------------------------

    /// <summary>The keys of the questions that are asked, given the answers. A question is asked when its condition holds on the answers to
    /// the questions asked before it; an answer to a hidden question counts as no answer, so hiding one question can hide those after it.</summary>
    public static HashSet<string> VisibleKeys(QuestionnaireDefinition definition, IReadOnlyDictionary<string, JsonElement> answers)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        var effective = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var q in definition.Questions)
        {
            if (q.ShowWhen is not null && !Evaluate(q.ShowWhen, effective))
                continue;

            visible.Add(q.Key);
            if (answers.TryGetValue(q.Key, out var answer) && IsAnswered(answer))
                effective[q.Key] = answer;
        }

        return visible;
    }

    /// <summary>The answers that belong: only to questions that exist and are currently asked, and only those that are actually answered.
    /// This is what gets stored, so a hidden question's old answer never lingers.</summary>
    public static Dictionary<string, JsonElement> Prune(QuestionnaireDefinition definition, IReadOnlyDictionary<string, JsonElement> answers)
    {
        var visible = VisibleKeys(definition, answers);
        return answers
            .Where(a => visible.Contains(a.Key) && IsAnswered(a.Value))
            .ToDictionary(a => a.Key, a => a.Value.Clone(), StringComparer.Ordinal);
    }

    public static bool IsAnswered(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(p => IsAnswered(p.Value)),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
        _ => false
    };

    public static bool Evaluate(ConditionDef condition, IReadOnlyDictionary<string, JsonElement> answers)
    {
        if (condition.All is not null)
            return condition.All.All(c => Evaluate(c, answers));
        if (condition.Any is not null)
            return condition.Any.Any(c => Evaluate(c, answers));

        var has = answers.TryGetValue(condition.QuestionKey ?? string.Empty, out var answer);
        var value = condition.Value ?? default;

        return condition.Operator switch
        {
            ConditionOperator.Answered => has,
            ConditionOperator.NotAnswered => !has,
            ConditionOperator.Equal => has && SameValue(answer, value),
            ConditionOperator.NotEqual => !has || !SameValue(answer, value),
            ConditionOperator.In => has && value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(v => SameValue(answer, v)),
            ConditionOperator.Contains => has && ContainsValue(answer, value),
            ConditionOperator.GreaterThan => has && TryNumber(answer, out var a) && TryNumber(value, out var b) && a > b,
            ConditionOperator.LessThan => has && TryNumber(answer, out var a2) && TryNumber(value, out var b2) && a2 < b2,
            _ => false
        };
    }

    private static bool SameValue(JsonElement answer, JsonElement value)
    {
        if (answer.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String)
            return string.Equals(answer.GetString()?.Trim(), value.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (TryNumber(answer, out var a) && TryNumber(value, out var b))
            return a == b;
        if (answer.ValueKind is JsonValueKind.True or JsonValueKind.False && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return answer.ValueKind == value.ValueKind;
        return false;
    }

    private static bool ContainsValue(JsonElement answer, JsonElement value)
    {
        if (answer.ValueKind == JsonValueKind.Array)
            return answer.EnumerateArray().Any(item => SameValue(item, value));
        if (answer.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String)
            return answer.GetString()!.Contains(value.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool TryNumber(JsonElement e, out decimal number)
    {
        number = 0;
        if (e.ValueKind == JsonValueKind.Number)
            return e.TryGetDecimal(out number);
        return e.ValueKind == JsonValueKind.String && decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    // ----- checking answers ------------------------------------------------------------------------------------------------------------

    /// <summary>Whether each given answer is acceptable for its question. Lenient about what is missing, so a half-finished form can be saved.</summary>
    public static List<QuestionnaireIssue> ValidateForSave(QuestionnaireDefinition definition, IReadOnlyDictionary<string, JsonElement> answers)
    {
        var issues = new List<QuestionnaireIssue>();
        var byKey = definition.Questions.ToDictionary(q => q.Key, StringComparer.Ordinal);

        foreach (var (key, value) in answers)
        {
            if (!byKey.TryGetValue(key, out var q))
            {
                issues.Add(new(key, "There is no such question."));
                continue;
            }

            if (!IsAnswered(value) && value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            var problem = ValueProblem(q, value);
            if (problem is not null)
                issues.Add(new(key, problem));
        }

        return issues;
    }

    /// <summary>The save checks plus: every question that is asked and required has an answer (and every required field of a structured
    /// answer is filled). A question that is not asked is never required.</summary>
    public static List<QuestionnaireIssue> ValidateForComplete(QuestionnaireDefinition definition, IReadOnlyDictionary<string, JsonElement> answers)
    {
        var issues = ValidateForSave(definition, answers);
        var visible = VisibleKeys(definition, answers);

        foreach (var q in definition.Questions.Where(q => visible.Contains(q.Key)))
        {
            var answered = answers.TryGetValue(q.Key, out var value) && IsAnswered(value);
            if (!answered)
            {
                if (q.Required)
                    issues.Add(new(q.Key, "This question needs an answer."));
                continue;
            }

            if (q.Type == QuestionType.Structured)
            {
                foreach (var field in q.Fields.Where(f => f.Required))
                {
                    var present = value.TryGetProperty(field.Key, out var fv) && IsAnswered(fv);
                    if (!present)
                        issues.Add(new($"{q.Key}.{field.Key}", $"{field.Label} needs an answer."));
                }
            }
        }

        return issues;
    }

    private static string? ValueProblem(QuestionDef q, JsonElement value)
    {
        switch (q.Type)
        {
            case QuestionType.ShortText:
            case QuestionType.LongText:
            {
                if (value.ValueKind != JsonValueKind.String)
                    return "Answer with text.";
                var max = q.MaxLength ?? (q.Type == QuestionType.ShortText ? DefaultShortTextLength : DefaultLongTextLength);
                return value.GetString()!.Length > max ? $"Keep this to {max} characters or fewer." : null;
            }
            case QuestionType.Number:
                return NumberProblem(value, q.Min, q.Max);
            case QuestionType.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Answer yes or no.";
            case QuestionType.SingleChoice:
                return value.ValueKind == JsonValueKind.String && q.Options.Any(o => string.Equals(o.Value, value.GetString(), StringComparison.OrdinalIgnoreCase))
                    ? null
                    : "Choose one of the listed options.";
            case QuestionType.MultiChoice:
            {
                if (value.ValueKind != JsonValueKind.Array)
                    return "Choose from the listed options.";
                var picked = value.EnumerateArray().ToList();
                if (picked.Any(p => p.ValueKind != JsonValueKind.String || !q.Options.Any(o => string.Equals(o.Value, p.GetString(), StringComparison.OrdinalIgnoreCase))))
                    return "Choose only from the listed options.";
                return picked.Select(p => p.GetString()!.ToLowerInvariant()).Distinct().Count() != picked.Count ? "An option was chosen twice." : null;
            }
            case QuestionType.Structured:
            {
                if (value.ValueKind != JsonValueKind.Object)
                    return "Answer with the listed fields.";
                foreach (var prop in value.EnumerateObject())
                {
                    var field = q.Fields.FirstOrDefault(f => f.Key == prop.Name);
                    if (field is null)
                        return $"'{prop.Name}' is not a field of this question.";
                    if (!IsAnswered(prop.Value) && prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.String)
                        continue;
                    var problem = field.Type switch
                    {
                        QuestionType.Number => NumberProblem(prop.Value, null, null),
                        QuestionType.Boolean => prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Answer yes or no.",
                        _ => prop.Value.ValueKind == JsonValueKind.String
                            ? (prop.Value.GetString()!.Length > DefaultShortTextLength ? $"Keep this to {DefaultShortTextLength} characters or fewer." : null)
                            : "Answer with text."
                    };
                    if (problem is not null)
                        return $"{field.Label}: {problem}";
                }
                return null;
            }
            default:
                return "Unknown question type.";
        }
    }

    private static string? NumberProblem(JsonElement value, decimal? min, decimal? max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var n))
            return "Answer with a number.";
        if (min is { } lo && n < lo) return $"Must be at least {lo.ToString(CultureInfo.InvariantCulture)}.";
        if (max is { } hi && n > hi) return $"Must be at most {hi.ToString(CultureInfo.InvariantCulture)}.";
        return null;
    }

    // ----- facts ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>One fact a set of answers states: the question it came from, and the words.</summary>
    public sealed record GeneratedFact(string SourceRef, string Category, string Statement);

    /// <summary>The facts the answers produce, one per asked, answered question that has a fact rule.</summary>
    public static List<GeneratedFact> FactsFrom(QuestionnaireDefinition definition, IReadOnlyDictionary<string, JsonElement> answers)
    {
        var visible = VisibleKeys(definition, answers);
        var facts = new List<GeneratedFact>();

        foreach (var q in definition.Questions.Where(q => q.Fact is not null && visible.Contains(q.Key)))
        {
            if (!answers.TryGetValue(q.Key, out var value) || !IsAnswered(value))
                continue;

            var words = InWords(q, value);
            if (string.IsNullOrWhiteSpace(words))
                continue;

            var statement = q.Fact!.Template.Replace("{value}", words, StringComparison.Ordinal).Trim();
            // The definition limits make this unreachable; it is the last line of defence so a fact can never overflow its column.
            if (statement.Length > MaxFactStatementLength)
                statement = statement[..MaxFactStatementLength];
            facts.Add(new GeneratedFact(q.Key, q.Fact.Category.Trim(), statement));
        }

        return facts;
    }

    /// <summary>An answer as a person would say it: option labels rather than values, "Yes" / "No", "Label: value" for each field.</summary>
    public static string InWords(QuestionDef q, JsonElement value)
    {
        switch (q.Type)
        {
            case QuestionType.Boolean:
                return value.ValueKind == JsonValueKind.True ? "yes" : "no";
            case QuestionType.Number:
                return value.TryGetDecimal(out var n) ? n.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
            case QuestionType.SingleChoice:
                return LabelFor(q, value.GetString());
            case QuestionType.MultiChoice:
                return JoinWithAnd(value.EnumerateArray().Select(v => LabelFor(q, v.GetString())).ToList());
            case QuestionType.Structured:
            {
                var parts = new List<string>();
                foreach (var field in q.Fields)
                {
                    if (!value.TryGetProperty(field.Key, out var fv) || !IsAnswered(fv))
                        continue;
                    var text = fv.ValueKind switch
                    {
                        JsonValueKind.True => "yes",
                        JsonValueKind.False => "no",
                        JsonValueKind.Number => fv.GetDecimal().ToString("0.##", CultureInfo.InvariantCulture),
                        _ => fv.GetString()?.Trim() ?? string.Empty
                    };
                    parts.Add($"{field.Label}: {text}");
                }
                return string.Join("; ", parts);
            }
            default:
                return value.GetString()?.Trim() ?? string.Empty;
        }
    }

    private static string LabelFor(QuestionDef q, string? value) =>
        q.Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value ?? string.Empty;

    private static string JoinWithAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
    };

    // ----- answers as stored ---------------------------------------------------------------------------------------------------------

    public static Dictionary<string, JsonElement> ReadAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions)
                   ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A stored answer set that cannot be read is treated as empty rather than breaking the screen; the tenant simply answers again.
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
    }

    public static string WriteAnswers(IReadOnlyDictionary<string, JsonElement> answers) => JsonSerializer.Serialize(answers, JsonOptions);

    /// <summary>A starting definition a platform admin can edit: generic, a little of every question type, and a condition.</summary>
    public static string StarterDefinitionJson()
    {
        var definition = new QuestionnaireDefinition
        {
            Questions =
            {
                new QuestionDef
                {
                    Key = "business_name", Type = QuestionType.ShortText, Prompt = "What is your business called?", Required = true, PrefillFrom = "Name",
                    Fact = new FactRule { Category = "About", Template = "The business is called {value}." }
                },
                new QuestionDef
                {
                    Key = "what_we_do", Type = QuestionType.LongText, Prompt = "In a few sentences, what do you do and who for?", Required = true,
                    PrefillFrom = "BusinessDescription", Fact = new FactRule { Category = "About", Template = "What we do: {value}" }
                },
                new QuestionDef
                {
                    Key = "customer_types", Type = QuestionType.MultiChoice, Prompt = "Who are your customers?", Required = true,
                    Options =
                    {
                        new OptionDef { Value = "individuals", Label = "Individuals" },
                        new OptionDef { Value = "families", Label = "Families" },
                        new OptionDef { Value = "businesses", Label = "Businesses" }
                    },
                    Fact = new FactRule { Category = "Customers", Template = "Our customers are {value}." }
                },
                new QuestionDef
                {
                    Key = "has_offer", Type = QuestionType.Boolean, Prompt = "Do you have a current offer or discount?"
                },
                new QuestionDef
                {
                    Key = "offer_details", Type = QuestionType.ShortText, Prompt = "What is the offer?", Required = true,
                    ShowWhen = new ConditionDef { QuestionKey = "has_offer", Operator = ConditionOperator.Equal, Value = JsonDocument.Parse("true").RootElement.Clone() },
                    Fact = new FactRule { Category = "Offers", Template = "Current offer: {value}" }
                },
                new QuestionDef
                {
                    Key = "opening_hours", Type = QuestionType.Structured, Prompt = "When are you open?",
                    Fields =
                    {
                        new FieldDef { Key = "weekdays", Label = "Monday to Friday", Required = true },
                        new FieldDef { Key = "saturday", Label = "Saturday" },
                        new FieldDef { Key = "sunday", Label = "Sunday" }
                    },
                    Fact = new FactRule { Category = "Contact", Template = "Opening hours - {value}." }
                }
            }
        };

        return JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
    }
}
