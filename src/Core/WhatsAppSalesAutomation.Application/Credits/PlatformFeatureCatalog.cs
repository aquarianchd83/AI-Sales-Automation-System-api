using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

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
    public static readonly IReadOnlyList<PlatformFeature> TrialFeatures = new[] { PlatformFeature.WhatsAppMessaging, PlatformFeature.AiConversations };

    public static readonly IReadOnlyList<PlatformFeature> PremiumFeatures = All.Select(f => f.Feature).ToList();
}
