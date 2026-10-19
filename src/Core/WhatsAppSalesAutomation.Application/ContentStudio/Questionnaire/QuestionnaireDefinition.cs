using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>The kinds of answer a question can take. Written as a word in the stored definition and in the API.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestionType
{
    ShortText,
    LongText,
    Number,
    Boolean,
    SingleChoice,
    MultiChoice,

    /// <summary>A small fixed set of named fields answered together - opening hours, an address - kept as one object.</summary>
    Structured
}

/// <summary>How a condition compares what the tenant answered with a value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConditionOperator
{
    Equal,
    NotEqual,

    /// <summary>The answer is any one of the listed values.</summary>
    In,

    /// <summary>A multiple-choice answer includes the value, or a text answer contains it.</summary>
    Contains,
    Answered,
    NotAnswered,
    GreaterThan,
    LessThan
}

/// <summary>One questionnaire version's questions, in the order they are asked.</summary>
public sealed class QuestionnaireDefinition
{
    public List<QuestionDef> Questions { get; set; } = new();
}

public sealed class QuestionDef
{
    /// <summary>Stable id of the question, lower-case letters, digits and underscores. Answers and facts refer to it, so it is never reused for a different question.</summary>
    public string Key { get; set; } = string.Empty;

    public QuestionType Type { get; set; }

    public string Prompt { get; set; } = string.Empty;

    /// <summary>A plain-words explanation of why we ask or what a good answer looks like.</summary>
    public string? HelpText { get; set; }

    /// <summary>A short sample answer, shown beside the question so a non-technical tenant can see what is wanted.</summary>
    public string? Example { get; set; }

    /// <summary>Questions with the same group are shown together as one small step ("About your business", "Brand"). Order of first appearance
    /// is the order of the steps. Null = the question forms a group of its own.</summary>
    public string? Group { get; set; }

    public bool Required { get; set; }

    /// <summary>When false (the default) the tenant may answer "I don't know", "I need help" or "Not applicable" instead of inventing something,
    /// and that counts as answered. True makes the question need a real answer - for the few where a guess or a skip is no use at all.</summary>
    public bool RequireKnown { get; set; }

    /// <summary>Longest text answer. Defaults to 200 for ShortText and 4000 for LongText.</summary>
    public int? MaxLength { get; set; }

    public decimal? Min { get; set; }

    public decimal? Max { get; set; }

    public List<OptionDef> Options { get; set; } = new();

    public List<FieldDef> Fields { get; set; } = new();

    /// <summary>The question is asked only when this is true of the answers given so far. Null = always asked.</summary>
    public ConditionDef? ShowWhen { get; set; }

    /// <summary>Starts a text answer from what the tenant's business profile already says, so nobody is asked twice. One of the names in
    /// <see cref="QuestionnaireEngine.PrefillSources"/>.</summary>
    public string? PrefillFrom { get; set; }

    /// <summary>When set, a completed answer becomes a draft business fact the tenant then verifies.</summary>
    public FactRule? Fact { get; set; }
}

public sealed class OptionDef
{
    public string Value { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

/// <summary>One named field of a Structured question. Text, Number and Boolean only.</summary>
public sealed class FieldDef
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public QuestionType Type { get; set; } = QuestionType.ShortText;

    public bool Required { get; set; }
}

/// <summary>A test on earlier answers: either one comparison (QuestionKey + Operator + Value), or All / Any of several.</summary>
public sealed class ConditionDef
{
    public string? QuestionKey { get; set; }

    public ConditionOperator? Operator { get; set; }

    public JsonElement? Value { get; set; }

    public List<ConditionDef>? All { get; set; }

    public List<ConditionDef>? Any { get; set; }
}

/// <summary>How an answer becomes a business fact: <see cref="Template"/> with "{value}" replaced by the answer in words.</summary>
public sealed class FactRule
{
    public string Category { get; set; } = string.Empty;

    public string Template { get; set; } = string.Empty;

    /// <summary>What the fact is about within its category ("Services"). Optional.</summary>
    public string? Subject { get; set; }

    /// <summary>When or where the fact holds. Optional.</summary>
    public string? Context { get; set; }

    /// <summary>For a fact that goes out of date (an offer, a price): how many days after it is confirmed it needs confirming again. Optional.</summary>
    public int? ValidDays { get; set; }
}

/// <summary>The three ways to answer without giving an answer. Stored as <c>{"$special":"unknown"}</c>; the leading "$" cannot clash with a
/// structured field key, which may not contain one.</summary>
public static class SpecialAnswer
{
    public const string Property = "$special";
    public const string Unknown = "unknown";
    public const string NeedHelp = "need_help";
    public const string NotApplicable = "not_applicable";

    public static readonly IReadOnlyList<string> All = new[] { Unknown, NeedHelp, NotApplicable };
}

/// <summary>One thing wrong with a definition or an answer, keyed to the question (and field) it concerns.</summary>
public sealed record QuestionnaireIssue(string Key, string Message);

/// <summary>Thrown when a definition cannot be read or is not valid; carries every problem found, not just the first.</summary>
public sealed class QuestionnaireDefinitionException : Exception
{
    public QuestionnaireDefinitionException(IReadOnlyList<QuestionnaireIssue> issues)
        : base(string.Join(" ", issues.Select(i => $"{i.Key}: {i.Message}")))
    {
        Issues = issues;
    }

    public IReadOnlyList<QuestionnaireIssue> Issues { get; }
}
