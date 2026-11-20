using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// The starter AI model catalogue (Platform Admin > AI Provider Models): the Claude, ChatGPT and Gemini models the application is configured to
/// use, with their prices, a cost tier and the scenario each suits. Insert-only and idempotent, keyed by (Provider, ModelId) like
/// <see cref="PlanSeeder"/>: a model that already exists is left exactly as a platform admin edited it, and one an admin deleted comes back only if
/// it is missing on the next boot, so retire a model with IsActive rather than deleting it.
/// <para>
/// Prices are USD per 1M tokens and match the rate tables in <c>LeadDiscoveryPricingOptions</c> / <c>AiPricingOptions</c>; context and output limits are
/// left blank where the provider's figure is not pinned down here. Cost tier is set from the output price: up to $5 is Cheap, up to $15 Average,
/// above that Expensive. Review all of it against the providers' current price pages.
/// </para>
/// </summary>
public static class AiProviderModelSeeder
{
    private sealed record Seed(
        string Provider, string DisplayName, string ModelId, AiModelType Type, AiModelCostTier Tier,
        int? Context, int? MaxOutput, decimal? InputPrice, decimal? OutputPrice, bool IsDefault, string Description);

    private static readonly Seed[] Models =
    {
        // Anthropic (Claude)
        new("Anthropic", "Claude Haiku 4.5", "claude-haiku-4-5-20251001", AiModelType.Chat, AiModelCostTier.Cheap, 200_000, 64_000, 1m, 5m, true,
            "Use for high-volume, latency-sensitive work: customer chat replies, intent and sentiment classification, lead qualification, keyword extraction and short summaries. The default Claude choice when cost matters more than depth of reasoning."),
        new("Anthropic", "Claude Sonnet 5", "claude-sonnet-5", AiModelType.Chat, AiModelCostTier.Average, null, null, 2m, 10m, false,
            "Use for the balanced middle: multi-step lead research and discovery, drafting campaign and marketing copy, answering from the knowledge base with citations, and reading long product documents. Pick it when Haiku's answers are not good enough but Opus is overkill."),
        new("Anthropic", "Claude Opus 5", "claude-opus-5", AiModelType.Chat, AiModelCostTier.Expensive, null, null, 5m, 25m, false,
            "Use sparingly, for the hardest tasks: complex multi-source research, nuanced long-form scripts and story writing, difficult reasoning or tricky edge cases that cheaper models get wrong. Do not use it for routine chat or bulk jobs - it costs several times more."),

        // OpenAI (ChatGPT)
        new("OpenAI", "GPT-5 nano", "gpt-5-nano", AiModelType.Chat, AiModelCostTier.Cheap, 400_000, 128_000, 0.05m, 0.4m, true,
            "Use for the cheapest, simplest jobs at very high volume: classification, tagging, routing, keyword extraction, short rewrites and reply suggestions. The lowest-cost default for customer chat."),
        new("OpenAI", "GPT-5 mini", "gpt-5-mini", AiModelType.Chat, AiModelCostTier.Cheap, 400_000, 128_000, 0.25m, 2m, false,
            "Use as the everyday workhorse: conversational replies, lead discovery runs, knowledge-base answers and content drafts at a low price. A good step up from nano when answers need more accuracy and still must stay cheap."),
        new("OpenAI", "GPT-5", "gpt-5", AiModelType.Chat, AiModelCostTier.Average, 400_000, 128_000, 1.25m, 10m, false,
            "Use when quality matters more than price: complex reasoning, long documents, detailed research and polished marketing or video-script writing. Reserve it for tasks the mini model gets wrong rather than running all traffic on it."),
        new("OpenAI", "Text Embedding 3 Small", "text-embedding-3-small", AiModelType.Embedding, AiModelCostTier.Cheap, 8_191, null, 0.02m, null, true,
            "Use to turn knowledge-base text and customer questions into vectors for semantic search. Very cheap, so it is the right default for embedding every article chunk."),
        new("OpenAI", "GPT Image 1", "gpt-image-1", AiModelType.Image, AiModelCostTier.Expensive, null, null, null, null, true,
            "Use to generate still pictures, such as the scene frames in the cinematic story studio and marketing visuals. Billed per picture rather than per token, and costs far more than any text model, so generate only after the user confirms the prompt."),

        // Google (Gemini)
        new("Google", "Gemini 2.5 Flash-Lite", "gemini-2.5-flash-lite", AiModelType.Chat, AiModelCostTier.Cheap, 1_048_576, 65_536, 0.1m, 0.4m, false,
            "Use for the lowest-cost bulk work: classification, extraction, tagging and quick replies. Choose it for lead discovery when volume is high and each lookup is simple; note Google Search grounding is billed separately."),
        new("Google", "Gemini 2.5 Flash", "gemini-2.5-flash", AiModelType.Chat, AiModelCostTier.Cheap, 1_048_576, 65_536, 0.3m, 2.5m, true,
            "Use as the general-purpose Gemini model: customer chat, lead discovery with Google Search grounding, summarising long documents and drafting content. Fast and inexpensive, with a very large context window."),
        new("Google", "Gemini 2.5 Pro", "gemini-2.5-pro", AiModelType.Chat, AiModelCostTier.Average, 1_048_576, 65_536, 1.25m, 10m, false,
            "Use for harder reasoning and very long inputs: deep multi-step research, analysing large product catalogues or documents, and careful long-form writing. Switch to it only where Flash falls short."),
        new("Google", "Text Embedding 004", "text-embedding-004", AiModelType.Embedding, AiModelCostTier.Cheap, 2_048, null, null, null, true,
            "Use to embed knowledge-base text and questions for semantic search when Gemini is the embedding provider. Priced at or near zero, so use it for every chunk."),
    };

    /// <summary>The tasks each seeded model is recommended for (AiModelTasks keys), by model id. Cheap models take the high-volume, simple jobs; the stronger ones the document and creative work.</summary>
    private static readonly Dictionary<string, string[]> BestFor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-haiku-4-5-20251001"] = new[] { "customer-replies", "business-keywords", "description-rewrite", "discovery-keywords", "information-extraction" },
        ["claude-sonnet-5"] = new[] { "document-analysis", "scanned-pages", "discover-customers" },
        ["claude-opus-5"] = new[] { "content-studio" },
        ["gpt-5-nano"] = new[] { "customer-replies", "business-keywords", "discovery-keywords" },
        ["gpt-5-mini"] = new[] { "description-rewrite", "information-extraction", "scanned-pages", "discover-customers" },
        ["gpt-5"] = new[] { "document-analysis", "content-studio" },
        ["gemini-2.5-flash-lite"] = new[] { "business-keywords", "discovery-keywords", "information-extraction" },
        ["gemini-2.5-flash"] = new[] { "customer-replies", "description-rewrite", "scanned-pages", "discover-customers" },
        ["gemini-2.5-pro"] = new[] { "document-analysis", "content-studio" },
    };

    private static string BestForOf(string modelId) => BestFor.TryGetValue(modelId, out var tasks) ? AiModelTasks.Join(tasks) : string.Empty;

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var current = await db.AiProviderModels.ToListAsync();
        var existing = current.Select(m => (m.Provider.ToLowerInvariant(), m.ModelId.ToLowerInvariant())).ToHashSet();

        // Models seeded before "Best for" existed get their recommendations now; one that already has any is left as the admin set it.
        var added = false;
        foreach (var m in current.Where(m => string.IsNullOrEmpty(m.BestFor) && BestFor.ContainsKey(m.ModelId)))
        {
            m.BestFor = BestForOf(m.ModelId);
            added = true;
        }

        // A catalogue an admin already curated keeps its own defaults: a seeded default is only offered for a provider and type with none.
        var hasDefault = (await db.AiProviderModels.Where(m => m.IsDefault).Select(m => new { m.Provider, m.ModelType }).ToListAsync())
            .Select(m => (m.Provider.ToLowerInvariant(), m.ModelType))
            .ToHashSet();

        foreach (var s in Models)
        {
            if (existing.Contains((s.Provider.ToLowerInvariant(), s.ModelId.ToLowerInvariant())))
                continue;

            var makeDefault = s.IsDefault && hasDefault.Add((s.Provider.ToLowerInvariant(), s.Type));
            db.AiProviderModels.Add(new AiProviderModel
            {
                Provider = s.Provider,
                DisplayName = s.DisplayName,
                ModelId = s.ModelId,
                ModelType = s.Type,
                CostTier = s.Tier,
                BestFor = BestForOf(s.ModelId),
                ContextWindowTokens = s.Context,
                MaxOutputTokens = s.MaxOutput,
                InputPricePer1MUsd = s.InputPrice,
                OutputPricePer1MUsd = s.OutputPrice,
                Description = s.Description,
                IsActive = true,
                IsDefault = makeDefault
            });
            added = true;
        }

        if (added)
            await db.SaveChangesAsync();
    }
}
