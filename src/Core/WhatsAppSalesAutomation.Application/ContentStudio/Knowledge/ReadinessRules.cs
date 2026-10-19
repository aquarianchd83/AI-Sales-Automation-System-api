using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;

/// <summary>
/// What the studio must know before it may write a given kind of content, as data: the platform operator edits it (Platform &gt;
/// Configuration &gt; Content Studio &gt; ReadinessRules) without a release. A <see cref="RequirementDef"/> says what evidence counts
/// (which fact categories, how many facts, whether proof is needed, how fresh); a <see cref="ContentTypeDef"/> says which requirements
/// matter for one kind of content, how much each is worth, and which are critical.
/// </summary>
public sealed class ReadinessRuleSet
{
    /// <summary>How much a fact the tenant stated but nobody verified is worth, 0 to 1. Never 1: only a verified fact is fully trusted.</summary>
    public double DraftCredit { get; set; } = 0.4;

    public List<RequirementDef> Requirements { get; set; } = new();

    public List<ContentTypeDef> ContentTypes { get; set; } = new();
}

public sealed class RequirementDef
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Facts in any of these categories count towards the requirement (case-insensitive).</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>Questionnaire question keys the requirement is about: their facts count too, and an "I don't know" answer to one is reported as such.</summary>
    public List<string> SourceRefs { get; set; } = new();

    /// <summary>How many separate facts it takes to satisfy the requirement.</summary>
    public int MinFacts { get; set; } = 1;

    /// <summary>When true a fact counts only if the tenant recorded how they know it (see BusinessFact.Evidence).</summary>
    public bool RequiresEvidence { get; set; }

    /// <summary>A verified fact older than this many days (counted from when it was verified) no longer counts: it needs confirming again.</summary>
    public int? MaxAgeDays { get; set; }

    /// <summary>Whether the tenant may say "we don't need this". Never true for what a kind of content cannot do without.</summary>
    public bool Skippable { get; set; }

    /// <summary>The simple follow-up question put to the tenant when the requirement is not met.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Why the studio needs it, in the tenant's terms.</summary>
    public string Why { get; set; } = string.Empty;

    public string? Example { get; set; }
}

public sealed class ContentTypeDef
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The score (0-100) at which the kind of content is ready, provided every critical requirement is also met.</summary>
    public int MinScore { get; set; } = 70;

    public List<RequirementRef> Requirements { get; set; } = new();
}

public sealed class RequirementRef
{
    public string Key { get; set; } = string.Empty;

    public double Weight { get; set; } = 10;

    /// <summary>A critical requirement must be fully met - with verified facts - for the content to be ready, whatever the score.</summary>
    public bool Critical { get; set; }
}

public static class ReadinessRules
{
    public const int MaxRequirements = 60;
    public const int MaxContentTypes = 12;

    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9_]{0,59}$", RegexOptions.Compiled);

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The rule set the setting's value describes, or the built-in one when the value is empty. An unreadable or invalid value
    /// yields the built-in set plus a warning, so a typo in the console never switches readiness off for everyone.</summary>
    public static (ReadinessRuleSet Rules, string? Warning) Resolve(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (Default, null);

        try
        {
            var parsed = JsonSerializer.Deserialize<ReadinessRuleSet>(json, JsonOptions);
            if (parsed is null)
                return (Default, "The readiness rules setting is empty, so the built-in rules are being used.");

            var problems = Validate(parsed);
            return problems.Count == 0
                ? (parsed, null)
                : (Default, "The readiness rules setting is not valid, so the built-in rules are being used: " + string.Join(" ", problems.Take(5)));
        }
        catch (JsonException ex)
        {
            return (Default, $"The readiness rules setting is not valid JSON, so the built-in rules are being used: {ex.Message}");
        }
    }

    public static IReadOnlyList<string> Validate(ReadinessRuleSet rules)
    {
        var problems = new List<string>();

        if (rules.DraftCredit is < 0 or >= 1)
            problems.Add("DraftCredit must be at least 0 and less than 1.");
        if (rules.Requirements.Count is 0 or > MaxRequirements)
            problems.Add($"There must be between 1 and {MaxRequirements} requirements.");
        if (rules.ContentTypes.Count is 0 or > MaxContentTypes)
            problems.Add($"There must be between 1 and {MaxContentTypes} content types.");

        var requirementKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rules.Requirements)
        {
            if (!KeyPattern.IsMatch(r.Key ?? string.Empty))
                problems.Add($"Requirement key '{r.Key}' must be lower-case letters, digits and underscores.");
            else if (!requirementKeys.Add(r.Key))
                problems.Add($"Requirement key '{r.Key}' is used twice.");
            if (string.IsNullOrWhiteSpace(r.Label) || string.IsNullOrWhiteSpace(r.Question) || string.IsNullOrWhiteSpace(r.Why))
                problems.Add($"Requirement '{r.Key}' needs a label, a question and a reason.");
            if (r.Categories.Count == 0 && r.SourceRefs.Count == 0)
                problems.Add($"Requirement '{r.Key}' needs at least one category or source question.");
            if (r.MinFacts is < 1 or > 20)
                problems.Add($"Requirement '{r.Key}': MinFacts must be between 1 and 20.");
            if (r.MaxAgeDays is < 1 or > 3650)
                problems.Add($"Requirement '{r.Key}': MaxAgeDays must be between 1 and 3650.");
        }

        var typeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in rules.ContentTypes)
        {
            if (!KeyPattern.IsMatch(t.Key ?? string.Empty))
                problems.Add($"Content type key '{t.Key}' must be lower-case letters, digits and underscores.");
            else if (!typeKeys.Add(t.Key))
                problems.Add($"Content type key '{t.Key}' is used twice.");
            if (string.IsNullOrWhiteSpace(t.Label))
                problems.Add($"Content type '{t.Key}' needs a label.");
            if (t.MinScore is < 1 or > 100)
                problems.Add($"Content type '{t.Key}': MinScore must be between 1 and 100.");
            if (t.Requirements.Count == 0)
                problems.Add($"Content type '{t.Key}' needs at least one requirement.");
            foreach (var reference in t.Requirements)
            {
                if (!requirementKeys.Contains(reference.Key))
                    problems.Add($"Content type '{t.Key}' refers to unknown requirement '{reference.Key}'.");
                if (reference.Weight <= 0)
                    problems.Add($"Content type '{t.Key}': the weight of '{reference.Key}' must be above 0.");
            }
            if (t.Requirements.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != t.Requirements.Count)
                problems.Add($"Content type '{t.Key}' lists a requirement twice.");
        }

        return problems;
    }

    public static string DefaultJson => DefaultRulesJson;

    public static ReadinessRuleSet Default => JsonSerializer.Deserialize<ReadinessRuleSet>(DefaultRulesJson, JsonOptions)!;

    private const string DefaultRulesJson = """
    {
      "draftCredit": 0.4,
      "requirements": [
        { "key": "identity", "label": "What the business is and does", "categories": ["About"], "sourceRefs": ["business_description"], "minFacts": 1,
          "question": "In a sentence or two, what does your business do, and for whom?",
          "why": "Every video starts from who you are and what you do. Without it the studio would have to guess.",
          "example": "Clinic: 'Hum Pune mein ek eye clinic hain. Cataract surgery aur bachchon ki aankhon ki jaanch karte hain.'  |  Kapde ki dukaan: 'Hum Jaipur mein ladies kurti ka showroom hain, ready-made aur stitched dono.'" },
        { "key": "audience", "label": "Who your customers are", "categories": ["Audience"], "sourceRefs": ["target_audience"], "minFacts": 1,
          "question": "Who are your typical customers? (age, place, what they need)",
          "why": "It decides the tone, the examples and which problems the video speaks to.",
          "example": "Clinic: '30 se 60 saal ke naukri-pesha log jo din bhar screen par kaam karte hain.'  |  Kapde ki dukaan: '25 se 45 saal ki mahilayein, shaadi aur tyohaar ki shopping ke liye.'" },
        { "key": "services", "label": "Your main services or products", "categories": ["Services"], "sourceRefs": ["products_services"], "minFacts": 1,
          "question": "Name the main services or products you offer.",
          "why": "Content can only talk about what you really sell. Each one named is something the studio is allowed to mention.",
          "example": "Clinic: 'Cataract surgery, computer vision check-up, bachchon ki aankhon ki jaanch.'  |  Kapde ki dukaan: 'Cotton kurti, designer lehenga, dupatta.'" },
        { "key": "services_depth", "label": "What each service involves", "categories": ["Services", "Features", "Process"], "minFacts": 3,
          "question": "Pick one service and tell us what happens, who it suits and how long it takes.",
          "why": "An explainer needs real detail - steps, who it is for, what to expect - or it turns into vague marketing.",
          "example": "Clinic: 'Cataract surgery lagbhag 20 minute ki hoti hai, local anaesthesia se hoti hai, aur zyadatar mareez usi din ghar chale jaate hain.'  |  Kapde ki dukaan: 'Kurti ki stitching 5 din mein hoti hai; naap WhatsApp par bhej sakte hain.'" },
        { "key": "features_process", "label": "How it works, step by step", "categories": ["Features", "Process"], "minFacts": 2,
          "question": "Describe how one main feature or service works, step by step.",
          "why": "A demonstration shows the steps in order, so it needs them from you rather than from a guess.",
          "example": "Clinic: '1. Website par appointment book karein. 2. WhatsApp par reminder aata hai. 3. Clinic aakar jaanch karwayein. 4. Report usi din mil jaati hai.'" },
        { "key": "brand_voice", "label": "Your brand's tone", "categories": ["Brand"], "sourceRefs": ["brand_tone"], "minFacts": 1, "skippable": true,
          "question": "How would you describe the way your brand speaks? (friendly, formal, premium...)",
          "why": "It keeps every video sounding like you.",
          "example": "'Apnapan wala, saaf Hindi-English, bina bhaari shabdon ke.'" },
        { "key": "language", "label": "The language to write in", "categories": ["Language"], "sourceRefs": ["content_language"], "minFacts": 1,
          "question": "Which language(s) should your videos be in?",
          "why": "Scripts and voice-over are written in this language.",
          "example": "'Hinglish - Hindi aur English mix.'" },
        { "key": "contact", "label": "How customers reach you", "categories": ["Contact"], "minFacts": 1,
          "question": "How should a customer contact or book with you? (phone, website, address, opening hours)",
          "why": "A video that asks people to act needs the real way to do it.",
          "example": "'Call: 020 1234 5678. Website: sunriseclinic.in. Somvar se Shanivar, subah 9 se shaam 6 baje tak.'" },
        { "key": "pricing", "label": "Your prices", "categories": ["Pricing"], "minFacts": 1, "maxAgeDays": 180,
          "question": "What does your main service or product cost, or what is the starting price?",
          "why": "Prices in a video must be exactly right and current; a wrong price is worse than none.",
          "example": "Clinic: 'Consultation Rs 500. Cataract surgery Rs 25,000 per aankh se shuru.'  |  Kapde ki dukaan: 'Cotton kurti Rs 699 se shuru.'" },
        { "key": "offers", "label": "A current offer", "categories": ["Offers"], "minFacts": 1, "maxAgeDays": 90,
          "question": "What offer or discount is running now, and until when does it last?",
          "why": "An offer in a video has to be real and still running when people watch it.",
          "example": "Clinic: 'Senior citizens ke liye free eye check-up, 31 December tak.'  |  Kapde ki dukaan: 'Diwali sale: har kharid par 20% off, 5 November tak.'" },
        { "key": "customer_results", "label": "A real customer result", "categories": ["Results", "Case studies"], "sourceRefs": ["case_study_result"], "minFacts": 1, "requiresEvidence": true,
          "question": "Tell us one real result a customer got, and how you can prove it (a report, a signed note, a before/after record).",
          "why": "A result claim is only safe when you can prove it. The studio will not write a case study without something to back it.",
          "example": "Clinic: 'Ek mareez ki padhne wali nazar March mein surgery ke baad 6/36 se 6/9 ho gayi. Signed outcome report humare paas hai.'" },
        { "key": "customer_consent", "label": "The customer agreed to be featured", "categories": ["Consent"], "minFacts": 1, "requiresEvidence": true,
          "question": "Did the customer agree to their story being used? How do you know (a signed form, a message)?",
          "why": "Using someone's story without their permission is a legal and trust risk, so we need to know it is settled.",
          "example": "'12 March ko likhit sahmati form signed hua, file mein rakha hai.'" }
      ],
      "contentTypes": [
        { "key": "general_education", "label": "General educational content", "description": "Helpful, non-promotional videos about your field.", "minScore": 60,
          "requirements": [
            { "key": "identity", "weight": 30, "critical": true }, { "key": "services", "weight": 25, "critical": true },
            { "key": "audience", "weight": 20 }, { "key": "language", "weight": 10 }, { "key": "brand_voice", "weight": 15 } ] },
        { "key": "product_explainers", "label": "Product and service explainers", "description": "What you offer, who it is for, what to expect.", "minScore": 70,
          "requirements": [
            { "key": "identity", "weight": 15, "critical": true }, { "key": "services", "weight": 25, "critical": true }, { "key": "services_depth", "weight": 25 },
            { "key": "audience", "weight": 15 }, { "key": "contact", "weight": 10 }, { "key": "language", "weight": 10 } ] },
        { "key": "feature_demos", "label": "Feature demonstrations", "description": "Show how something works, step by step.", "minScore": 70,
          "requirements": [
            { "key": "services", "weight": 15, "critical": true }, { "key": "features_process", "weight": 35, "critical": true }, { "key": "identity", "weight": 15 },
            { "key": "audience", "weight": 15 }, { "key": "contact", "weight": 10 }, { "key": "language", "weight": 10 } ] },
        { "key": "offers_pricing", "label": "Offers and pricing", "description": "Videos that quote a price or promote an offer.", "minScore": 75,
          "requirements": [
            { "key": "pricing", "weight": 30, "critical": true }, { "key": "offers", "weight": 25, "critical": true }, { "key": "services", "weight": 15 },
            { "key": "identity", "weight": 10 }, { "key": "contact", "weight": 10 }, { "key": "language", "weight": 10 } ] },
        { "key": "case_studies", "label": "Case studies and customer results", "description": "Real customer stories and results. The strictest rules apply.", "minScore": 85,
          "requirements": [
            { "key": "customer_results", "weight": 35, "critical": true }, { "key": "customer_consent", "weight": 30, "critical": true }, { "key": "identity", "weight": 10 },
            { "key": "services", "weight": 10 }, { "key": "audience", "weight": 5 }, { "key": "language", "weight": 10 } ] }
      ]
    }
    """;
}
