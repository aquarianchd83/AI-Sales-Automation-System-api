using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>
/// What one free use of a feature is, for a plan's free uses of a feature it does not include (PlanFeatureTrial). <paramref name="ReferenceType"/>
/// is the spend that counts as a use: lead discovery counts the leads a run is charged for, not the AI research calls behind them. Null counts
/// every spend of the feature.
/// </summary>
public sealed record FeatureTrialUnit(string Singular, string Plural, string? ReferenceType)
{
    public string Format(int count) => $"{count} {(count == 1 ? Singular : Plural)}";
}

/// <summary>One feature as the app describes it: what it is called, and the spend scenarios that belong to it.</summary>
public sealed record PlatformFeatureInfo(PlatformFeature Feature, string Label, string Description, IReadOnlyList<CreditScenario> Scenarios);

/// <summary>
/// The features a plan can include, the credit spends that belong to each, and the plans the platform starts with. One wallet pays for every
/// feature; a feature's spend is what its scenarios cost. The numbers here are only where the platform starts - the credits each action costs are
/// edited on the platform admin's Credit costs screen, and a plan's credits and features on its plan editor.
/// </summary>
public static class PlatformFeatureCatalog
{
    public static readonly IReadOnlyList<PlatformFeatureInfo> All = new PlatformFeatureInfo[]
    {
        new(PlatformFeature.WhatsAppMessaging, "WhatsApp messaging", "Template messages sent by an agent, a campaign or a lead follow-up.",
            new[] { CreditScenario.WhatsAppTemplateSend }),
        new(PlatformFeature.AiConversations, "AI conversations", "The AI answering your customers in WhatsApp.",
            new[] { CreditScenario.CustomerChatReply }),
        new(PlatformFeature.LeadDiscovery, "Lead discovery", "Finding new leads for your business.",
            new[] { CreditScenario.LeadDiscovery }),
        new(PlatformFeature.ContentStudio, "Content Studio", "AI content generation and video rendering.",
            new[] { CreditScenario.ContentStudioGeneration, CreditScenario.ContentStudioVideoRender }),
        new(PlatformFeature.ProductKnowledge, "Product knowledge", "Reading your sources and drafting product knowledge with AI.",
            new[] { CreditScenario.ProductKnowledgeReading, CreditScenario.ProductKnowledgeAnswering }),
        // Campaign sends are paid as WhatsApp messaging, so this feature spends nothing of its own: it only gates access and the campaign limit.
        new(PlatformFeature.Campaigns, "Campaigns", "Bulk and scheduled WhatsApp campaigns. Sends are paid as WhatsApp messaging.",
            Array.Empty<CreditScenario>()),
    };

    public static PlatformFeatureInfo For(PlatformFeature feature) => All.Single(f => f.Feature == feature);

    /// <summary>The feature a spend scenario belongs to.</summary>
    public static PlatformFeature FeatureOf(CreditScenario scenario) => All.First(f => f.Scenarios.Contains(scenario)).Feature;

    // ----- the plans the platform starts with -------------------------------------------------------------------------------------------------

    public const string TrialPlanCode = "trial";
    public const string PremiumPlanCode = "premium";

    /// <summary>Credits a new Trial tenant starts with, once. The plan never expires, so neither do they.</summary>
    public const decimal TrialStarterCredits = 50m;

    /// <summary>Credits Premium tenants receive each month. Unspent credits carry over.</summary>
    public const decimal PremiumMonthlyCredits = 100m;

    /// <summary>The Trial plan is the minimum: you can message customers and have the AI answer them. Everything else is Premium.</summary>
    public static readonly IReadOnlyList<PlatformFeature> TrialFeatures = new[] { PlatformFeature.WhatsAppMessaging, PlatformFeature.AiConversations, PlatformFeature.Campaigns };

    public static readonly IReadOnlyList<PlatformFeature> PremiumFeatures = All.Select(f => f.Feature).ToList();

    // ----- free uses of a feature outside the plan --------------------------------------------------------------------------------------------

    /// <summary>What one free use of each feature is. A feature with no unit (Campaigns, which spends nothing of its own) cannot be given free uses.</summary>
    public static FeatureTrialUnit? TrialUnitFor(PlatformFeature feature) => feature switch
    {
        PlatformFeature.LeadDiscovery => new("lead", "leads", "LeadDiscoveryRun"),
        PlatformFeature.ContentStudio => new("AI generation", "AI generations", "StudioActivity"),
        PlatformFeature.ProductKnowledge => new("AI action", "AI actions", CreditScenarioCatalog.ProductKnowledgeReference),
        PlatformFeature.WhatsAppMessaging => new("message", "messages", "Message"),
        PlatformFeature.AiConversations => new("AI reply", "AI replies", null),
        _ => null
    };

    /// <summary>
    /// The free uses the Trial (Basic) plan starts with of each Premium-only feature, so a Basic tenant can try it before being asked to upgrade.
    /// Only where the platform starts: the platform admin changes them on the plan editor, and the seeder never overwrites a plan that has any.
    /// </summary>
    public static readonly IReadOnlyDictionary<PlatformFeature, int> TrialFreeUses = new Dictionary<PlatformFeature, int>
    {
        [PlatformFeature.LeadDiscovery] = 10,
        [PlatformFeature.ContentStudio] = 3,
        [PlatformFeature.ProductKnowledge] = 10,
    };
}
