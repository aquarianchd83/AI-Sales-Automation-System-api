using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Integrations;

/// <summary>One AI feature's engine, bound from "Integrations:Ai:{Feature}", kept by the Integrations page. Absent means the feature's default.</summary>
public class AiFeatureOption
{
    public string? Mode { get; set; }

    public string? Provider { get; set; }
}

/// <summary>Bound from "Integrations:Ai".</summary>
public class AiFeatureOptions
{
    public AiFeatureOption DiscoveryLead { get; set; } = new();

    public AiFeatureOption KnowledgeBase { get; set; } = new();

    public AiFeatureOption ContentGeneration { get; set; } = new();

    public AiFeatureOption VideoRenders { get; set; } = new();
    public AiFeatureOption ProductDocumentReading { get; set; } = new();
    public AiFeatureOption ProductScannedReading { get; set; } = new();
    public AiFeatureOption ProductSuggestedAnswers { get; set; } = new();
    public AiFeatureOption ProductQuestionnaire { get; set; } = new();
    public AiFeatureOption ProductGapCheck { get; set; } = new();
    public AiFeatureOption ProductKnowledgeDraft { get; set; } = new();
    public AiPolicyOptions Policy { get; set; } = new();

    public AiFeatureOption BusinessKeywords { get; set; } = new();

    public AiFeatureOption DiscoveryKeywords { get; set; } = new();

    public AiFeatureOption For(string feature) => feature switch
    {
        AiFeatures.DiscoveryLead => DiscoveryLead,
        AiFeatures.KnowledgeBase => KnowledgeBase,
        AiFeatures.ContentGeneration => ContentGeneration,
        AiFeatures.VideoRenders => VideoRenders,
        AiFeatures.ProductDocumentReading => ProductDocumentReading,
        AiFeatures.ProductScannedReading => ProductScannedReading,
        AiFeatures.ProductSuggestedAnswers => ProductSuggestedAnswers,
        AiFeatures.ProductQuestionnaire => ProductQuestionnaire,
        AiFeatures.ProductGapCheck => ProductGapCheck,
        AiFeatures.ProductKnowledgeDraft => ProductKnowledgeDraft,
        AiFeatures.BusinessKeywords => BusinessKeywords,
        AiFeatures.DiscoveryKeywords => DiscoveryKeywords,
        _ => new AiFeatureOption(),
    };
}

/// <summary>Tells a feature, per request, whether to use the simulator or a named third-party provider, so a save on the Integrations page is
/// live without a restart.</summary>
public class AiFeatureRouting : IAiFeatureRouting
{
    private readonly IOptionsSnapshot<AiFeatureOptions> _options;

    public AiFeatureRouting(IOptionsSnapshot<AiFeatureOptions> options)
    {
        _options = options;
    }

    public AiFeatureChoice Resolve(string feature)
    {
        var option = _options.Value.For(feature);
        var mode = string.IsNullOrWhiteSpace(option.Mode) ? AiFeatures.DefaultMode(feature) : option.Mode;
        var supported = AiFeatures.SupportedProviders(feature);
        var provider = PlatformAiFeatureSettingsService.Canonical(option.Provider) ?? supported.FirstOrDefault() ?? AiFeatures.Providers[0];
        var api = string.Equals(mode.Trim(), PlatformIntegrationSettingsService.ApiMode, StringComparison.OrdinalIgnoreCase);

        // Off exists only for the product-knowledge task groups; anywhere else an "Off" value is read as the simulator.
        if (string.Equals(mode.Trim(), AiFeatures.OffMode, StringComparison.OrdinalIgnoreCase) && AiFeatures.IsProductTaskGroup(feature))
            return new AiFeatureChoice(false, provider, IsOff: true);

        // A tool the feature cannot run on (saved earlier, or by a request that skipped the page) is never called: the simulator answers.
        return new AiFeatureChoice(api && supported.Contains(provider), provider);
    }
}

/// <summary>How product-knowledge AI tasks behave (the "Integrations:Ai:Policy" settings).</summary>
public class AiPolicyOptions
{
    public bool FallbackEnabled { get; set; }

    public string? FallbackOrder { get; set; } = "OpenAI,Google,Anthropic";

    public string SimulatorScenario { get; set; } = "Success";

    public decimal MaxCostPerCallUsd { get; set; } = 0.50m;

    public int TimeoutSeconds { get; set; } = 60;

    public int MaxRetriesPerProvider { get; set; } = 1;

    public int RetryDelayMs { get; set; } = 500;
}
