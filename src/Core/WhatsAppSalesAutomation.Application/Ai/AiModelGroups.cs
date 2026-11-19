using WhatsAppSalesAutomation.Application.Ai.Tasks;

namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>One group of AI tasks that can run on its own model.</summary>
/// <param name="Key">The name stored in settings: <c>AiProviders:TaskModels:{Key}:{Provider}</c>.</param>
/// <param name="AllowDefault">False for lead discovery: it always has a model saved (seeded from the discovery default), so the page offers no blank.</param>
/// <param name="DefaultLabel">What "nothing chosen" is called on the page: "Same as chat model", or "Default" for lead discovery.</param>
public sealed record AiModelGroup(string Key, string Title, string Description, IReadOnlyList<string> Tasks, string DefaultLabel = "Same as chat model", bool AllowDefault = true);

/// <summary>
/// The tasks the platform runs on AI, in the three groups an operator can give a model of their own: replying to customers wants a fast, cheap
/// model; reading and analysing product documents wants an accurate one (and one that accepts images); keyword suggestions and rewrites are
/// simple text jobs the cheapest model does well. A group with no choice for a provider runs on that provider's chat model, exactly as before.
/// Lead discovery is a fourth group: it researches with web search, so with nothing chosen it runs on the discovery default (the
/// <c>LeadDiscovery:Agent</c> models, which are deliberately not the chat model), and the agents read the choice from here.
/// </summary>
public static class AiModelGroups
{
    public const string CustomerReplies = "CustomerReplies";
    public const string ProductKnowledge = "ProductKnowledge";
    public const string KeywordsAndRewrites = "KeywordsAndRewrites";
    public const string LeadDiscovery = "LeadDiscovery";

    public static readonly IReadOnlyList<AiModelGroup> All = new AiModelGroup[]
    {
        new(CustomerReplies, "Customer replies", "Replying to customers in WhatsApp. Needs to be fast and cheap.",
            new[] { "Replying to customers in WhatsApp" }),
        new(ProductKnowledge, "Product knowledge", "Reading and analysing product material. Needs accuracy, and an image-capable model for scanned pages.",
            new[] { "Document and PDF analysis", "Product information extraction", "Scanned pages (image-capable)", "Questionnaires, drafts and story scripts" }),
        new(KeywordsAndRewrites, "Keywords and rewrites", "Simple text jobs. The cheapest model is enough.",
            new[] { "Domain keyword suggestions", "Business description rewrite", "Search keyword suggestions" }),
        new(LeadDiscovery, "Lead discovery", "Finding new businesses with web search. The model must support web search and function calling.",
            new[] { "Finding new businesses (web search agent)" }, "Default", AllowDefault: false),
    };

    /// <summary>The setting that holds a group's model on one provider; blank means that provider's chat model.</summary>
    public static string SettingKey(string group, string provider) => $"AiProviders:TaskModels:{group}:{provider}";

    /// <summary>The group an AI task belongs to, or null for a task that is not in one (it runs on the chat model).</summary>
    public static string? ForTask(string? task)
    {
        if (string.IsNullOrWhiteSpace(task))
            return null;

        if (task == AiTaskKinds.ConversationReply)
            return CustomerReplies;

        if (task is AiTaskKinds.DescriptionRefinement or AiTaskKinds.BusinessKeywordSuggestion or AiTaskKinds.DiscoveryKeywordSuggestion)
            return KeywordsAndRewrites;

        return AiTaskKinds.Product.Contains(task, StringComparer.Ordinal) ? ProductKnowledge : null;
    }

    public static string? Canonical(string? group) =>
        All.FirstOrDefault(g => string.Equals(g.Key, group?.Trim(), StringComparison.OrdinalIgnoreCase))?.Key;
}
