using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Ai.Tasks;

/// <summary>Every kind of AI work the application does: the product-knowledge engine's tasks, and the AI features that already existed before it
/// (customer chat, knowledge base search, suggestions, lead discovery). A provider is only asked for a task it is declared for.</summary>
public static class AiTaskKinds
{
    public const string DocumentAnalysis = "DocumentAnalysis";
    public const string ProductInfoExtraction = "ProductInfoExtraction";
    public const string KnowledgeConsolidation = "KnowledgeConsolidation";
    public const string GroundedQuestionAnswering = "GroundedQuestionAnswering";
    public const string CustomQuestionAnswering = "CustomQuestionAnswering";
    public const string QuestionnaireGeneration = "QuestionnaireGeneration";
    public const string BenefitAnalysis = "BenefitAnalysis";
    public const string MissingInformationDetection = "MissingInformationDetection";
    public const string ConflictIdentification = "ConflictIdentification";
    public const string SuggestedAnswerGeneration = "SuggestedAnswerGeneration";
    public const string KnowledgeDraft = "KnowledgeDraft";

    /// <summary>Reading a scanned page or image. Needs a model that accepts images, which is why it is a task of its own.</summary>
    public const string ScannedDocumentReading = "ScannedDocumentReading";

    // The AI features that existed before the product-knowledge engine. They keep their own code paths; their calls are recorded in the same
    // log so a provider's capability for them can be verified by real use.
    public const string ConversationReply = "ConversationReply";
    public const string KnowledgeEmbedding = "KnowledgeEmbedding";
    public const string BusinessKeywordSuggestion = "BusinessKeywordSuggestion";
    public const string DescriptionRefinement = "DescriptionRefinement";
    public const string DiscoveryKeywordSuggestion = "DiscoveryKeywordSuggestion";
    public const string LeadDiscovery = "LeadDiscovery";

    /// <summary>Not product work: the platform's own checks, recorded in the same log.</summary>
    public const string ConnectionCheck = "ConnectionCheck";
    public const string Benchmark = "Benchmark";

    public static readonly IReadOnlyList<string> Product = new[]
    {
        DocumentAnalysis, ProductInfoExtraction, KnowledgeConsolidation, GroundedQuestionAnswering, CustomQuestionAnswering,
        QuestionnaireGeneration, BenefitAnalysis, MissingInformationDetection, ConflictIdentification, SuggestedAnswerGeneration,
        KnowledgeDraft, ScannedDocumentReading
    };
}

/// <summary>
/// What each provider is <i>declared</i> able to do. A declaration is a promise the code makes (the adapter can send this kind of request),
/// not proof the provider's current model does it well: a capability counts as <i>verified</i> only once a real call for that task has
/// succeeded, which the Integrations page shows separately. The scanned-document task additionally depends on the chosen model accepting images.
/// </summary>
public static class AiCapabilities
{
    public const string Simulator = "Simulator";

    private static readonly IReadOnlyList<string> AllProviders = new[] { "OpenAI", "Anthropic", "Google" };

    public static IReadOnlyList<string> RealProviders => AllProviders;

    /// <summary>The simulator stands in for the product-knowledge tasks only; a real provider for every use whose code can call it.</summary>
    public static IReadOnlyList<string> DeclaredFor(string provider) =>
        string.Equals(provider, Simulator, StringComparison.OrdinalIgnoreCase)
            ? AiTaskKinds.Product
            : AiUses.All.Where(u => u.Providers.Contains(provider, StringComparer.OrdinalIgnoreCase)).Select(u => u.Task).ToList();

    public static bool IsDeclared(string provider, string task) =>
        DeclaredFor(provider).Contains(task, StringComparer.Ordinal) || task is AiTaskKinds.ConnectionCheck or AiTaskKinds.Benchmark;
}

/// <summary>One place the application uses AI, as the AI Providers screen lists it.</summary>
/// <param name="Area">Where it shows up: Customer chat, Onboarding and profile, Lead discovery or Product knowledge.</param>
/// <param name="Providers">The providers its code can call. Claude has no embeddings; the lead discovery agent is Claude's.</param>
public sealed record AiUse(string Task, string Area, string Title, IReadOnlyList<string> Providers);

/// <summary>Every AI use in the application - the list a provider's capabilities are checked against.</summary>
public static class AiUses
{
    public const string CustomerChat = "Customer chat";
    public const string Onboarding = "Onboarding and profile";
    public const string Discovery = "Lead discovery";
    public const string ProductKnowledge = "Product knowledge";

    private static readonly string[] Three = { "OpenAI", "Anthropic", "Google" };

    public static readonly IReadOnlyList<AiUse> All = new AiUse[]
    {
        new(AiTaskKinds.ConversationReply, CustomerChat, "Replying to customers in WhatsApp", Three),
        new(AiTaskKinds.KnowledgeEmbedding, CustomerChat, "Knowledge base search (embeddings)", new[] { "OpenAI", "Google" }),
        new(AiTaskKinds.BusinessKeywordSuggestion, Onboarding, "Domain keyword suggestions", Three),
        new(AiTaskKinds.DescriptionRefinement, Onboarding, "Business description rewrite", Three),
        new(AiTaskKinds.DiscoveryKeywordSuggestion, Discovery, "Search keyword suggestions", Three),
        new(AiTaskKinds.LeadDiscovery, Discovery, "Finding new businesses (web search agent)", new[] { "Anthropic" }),
        new(AiTaskKinds.DocumentAnalysis, ProductKnowledge, "Document and PDF analysis", Three),
        new(AiTaskKinds.ProductInfoExtraction, ProductKnowledge, "Product information extraction", Three),
        new(AiTaskKinds.ScannedDocumentReading, ProductKnowledge, "Reading scanned pages and images (needs an image-capable model)", Three),
        new(AiTaskKinds.SuggestedAnswerGeneration, ProductKnowledge, "Suggested answers", Three),
        new(AiTaskKinds.GroundedQuestionAnswering, ProductKnowledge, "Question answering from sources", Three),
        new(AiTaskKinds.CustomQuestionAnswering, ProductKnowledge, "Tenant-created questions", Three),
        new(AiTaskKinds.QuestionnaireGeneration, ProductKnowledge, "Questionnaire generation", Three),
        new(AiTaskKinds.BenefitAnalysis, ProductKnowledge, "Benefits and customer value", Three),
        new(AiTaskKinds.MissingInformationDetection, ProductKnowledge, "Missing-information detection", Three),
        new(AiTaskKinds.ConflictIdentification, ProductKnowledge, "Conflict identification", Three),
        new(AiTaskKinds.KnowledgeConsolidation, ProductKnowledge, "Knowledge consolidation", Three),
        new(AiTaskKinds.KnowledgeDraft, ProductKnowledge, "Draft knowledge file", Three),
    };

    public static IReadOnlyList<string> Tasks { get; } = All.Select(u => u.Task).ToList();
}

/// <summary>
/// Names the AI use a one-shot text call is for (keyword suggestions, the description rewrite), so the call can be recorded against it. Set by
/// the feature around its call: <c>using var _ = AiCallLabel.Use(AiTaskKinds.BusinessKeywordSuggestion);</c>. An unlabelled call is not recorded.
/// </summary>
public static class AiCallLabel
{
    private static readonly AsyncLocal<string?> Label = new();

    public static string? Current => Label.Value;

    public static IDisposable Use(string task)
    {
        var previous = Label.Value;
        Label.Value = task;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;

        public Restore(string? previous) => _previous = previous;

        public void Dispose() => Label.Value = _previous;
    }
}

public static class AiSimulatorScenarios
{
    public const string Success = "Success";
    public const string MissingEvidence = "MissingEvidence";
    public const string Conflict = "Conflict";
    public const string Malformed = "Malformed";
    public const string ProviderError = "ProviderError";

    public static readonly IReadOnlyList<string> All = new[] { Success, MissingEvidence, Conflict, Malformed, ProviderError };
}

public static class AiErrorCodes
{
    public const string NoProvider = "NO_PROVIDER";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string ProviderNotAllowed = "PROVIDER_NOT_ALLOWED";
    public const string CapabilityUnsupported = "CAPABILITY_UNSUPPORTED";
    public const string FeatureOff = "FEATURE_OFF";
    public const string CostLimit = "COST_LIMIT";
    public const string Timeout = "TIMEOUT";
    public const string RateLimited = "RATE_LIMITED";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string ProviderError = "PROVIDER_ERROR";
    public const string MalformedResponse = "MALFORMED_RESPONSE";
    public const string SimulatedProviderError = "SIMULATED_PROVIDER_ERROR";
}

/// <summary>The price of a provider's model, per million tokens, in US dollars. Entered by the admin; costs are estimates from it.</summary>
public sealed record ProviderPrice(decimal InputPer1MUsd, decimal OutputPer1MUsd)
{
    public decimal? Cost(int? inputTokens, int? outputTokens) =>
        inputTokens is null && outputTokens is null
            ? null
            : Math.Round(((inputTokens ?? 0) * InputPer1MUsd + (outputTokens ?? 0) * OutputPer1MUsd) / 1_000_000m, 6);
}

/// <summary>How tasks behave, read fresh for every run so a change on the Integrations page applies from the next request.</summary>
public sealed record AiTaskPolicy(
    bool FallbackEnabled,
    IReadOnlyList<string> FallbackOrder,
    string SimulatorScenario,
    decimal MaxCostPerCallUsd,
    int TimeoutSeconds,
    int MaxRetriesPerProvider,
    int RetryDelayMs,
    IReadOnlyDictionary<string, ProviderPrice> Prices)
{
    public ProviderPrice PriceOf(string provider) =>
        Prices.TryGetValue(provider, out var price) ? price : new ProviderPrice(0m, 0m);
}

public interface IAiTaskPolicyProvider
{
    AiTaskPolicy Get();
}

// ----- the low-level call -----------------------------------------------------------------------------------------------------------

/// <param name="ImageBase64">For <see cref="AiTaskKinds.ScannedDocumentReading"/>: one page image, base64. Null for text tasks.</param>
public sealed record AiCompletionRequest(
    string Provider, string System, string User, int MaxTokens, bool ExpectJson, int TimeoutSeconds, string? ImageBase64 = null, string? ImageMediaType = null);

/// <summary>What one provider call returned, normalised. Provider-specific detail stays in <see cref="ErrorMessage"/> (already short and scrubbed).</summary>
public sealed record AiCompletionResult(
    bool Success, string? Text, string? Model, int? InputTokens, int? OutputTokens, int DurationMs, string? ErrorCode, string? ErrorMessage, int? HttpStatus = null);

/// <summary>One call to one provider, with token counts and a normalised error. The only place that knows each vendor's wire format for tasks.</summary>
public interface IAiCompletionClient
{
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken = default);
}

// ----- the task runner --------------------------------------------------------------------------------------------------------------

/// <param name="Task">One of <see cref="AiTaskKinds"/>.</param>
/// <param name="AllowedProviders">When set (a tenant's data-processing permission), only these providers may be used - for the first choice and for fallbacks.</param>
/// <param name="Idempotent">False for a call with an external side effect: it is then never retried or re-sent to another provider.</param>
public sealed record AiTaskRequest(
    string Task,
    string System,
    string User,
    int MaxTokens = 1500,
    bool ExpectJson = true,
    Guid? TenantId = null,
    /// <summary>Null: the product-knowledge task group the task belongs to, whose switch on the Integrations page decides.</summary>
    string? Feature = null,
    IReadOnlyCollection<string>? AllowedProviders = null,
    bool Idempotent = true,
    string? ImageBase64 = null,
    string? ImageMediaType = null);

public sealed record AiAttempt(string Provider, bool Success, string? ErrorCode, string? ErrorMessage, int DurationMs);

/// <summary>
/// The outcome of a task. <see cref="Mode"/> says whether this came from a live provider or the simulator and is carried everywhere the
/// result goes: a simulator result is never presentable as a live one.
/// </summary>
public sealed record AiTaskResult(
    bool Success,
    string? Text,
    AiExecutionMode Mode,
    string Provider,
    string? Model,
    bool UsedFallback,
    string? ErrorCode,
    string? ErrorMessage,
    Guid CorrelationId,
    IReadOnlyList<AiAttempt> Attempts,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd)
{
    public bool IsSimulated => Mode == AiExecutionMode.Simulator;
}

public interface IAiTaskRunner
{
    /// <summary>
    /// Resolves the provider from the admin's choice for the feature, checks it is on, keyed, allowed and capable, runs the task, validates the
    /// reply, records every attempt, and - only if fallback is switched on - tries the next provider. Never substitutes the simulator for a
    /// live provider that failed.
    /// </summary>
    Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default);
}

public interface IAiExecutionLog
{
    Task WriteAsync(Domain.Entities.Platform.AiExecution execution, CancellationToken cancellationToken = default);
}
