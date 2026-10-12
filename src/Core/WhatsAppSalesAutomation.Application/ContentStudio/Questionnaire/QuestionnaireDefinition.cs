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

    public string? HelpText { get; set; }

    public bool Required { get; set; }

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
