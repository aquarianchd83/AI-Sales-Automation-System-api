using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio;

/// <summary>What one kind of studio activity costs in quota units. The single place that decides which quota a step
/// draws from, so a step cannot be charged to the wrong meter by a caller's typo.</summary>
public sealed record StudioChargeRule(QuotaType QuotaType, decimal DefaultUnits, string Label);

public static class StudioChargeCatalog
{
    private static readonly IReadOnlyDictionary<StudioActivityKind, StudioChargeRule> Rules = new Dictionary<StudioActivityKind, StudioChargeRule>
    {
        [StudioActivityKind.TopicIdeas] = new(QuotaType.ContentGenerations, 1m, "Topic ideas"),
        [StudioActivityKind.Script] = new(QuotaType.ContentGenerations, 1m, "Script"),
        [StudioActivityKind.SceneBreakdown] = new(QuotaType.ContentGenerations, 1m, "Scene breakdown"),
        [StudioActivityKind.VoiceOver] = new(QuotaType.ContentGenerations, 1m, "Voice-over"),
        [StudioActivityKind.StockAssetSearch] = new(QuotaType.ContentGenerations, 0m, "Stock image search"),
        [StudioActivityKind.ImageGeneration] = new(QuotaType.ContentGenerations, 2m, "Generated illustration"),
        [StudioActivityKind.VideoRender] = new(QuotaType.VideoRenders, 1m, "Video render"),
        [StudioActivityKind.Publish] = new(QuotaType.ContentGenerations, 0m, "Publish"),
        [StudioActivityKind.Other] = new(QuotaType.ContentGenerations, 1m, "Other"),
    };

    public static StudioChargeRule For(StudioActivityKind kind) => Rules[kind];

    public static string LabelFor(StudioActivityKind kind) => Rules[kind].Label;

    /// <summary>The quota types the studio draws from - what its balance cards show.</summary>
    public static readonly IReadOnlyList<QuotaType> QuotaTypes = new[] { QuotaType.ContentGenerations, QuotaType.VideoRenders };
}
