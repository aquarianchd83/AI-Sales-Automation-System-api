using FluentValidation;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The AI-backed features whose engine the platform admin chooses on the Integrations page.</summary>
public static class AiFeatures
{
    public const string DiscoveryLead = "DiscoveryLead";
    public const string KnowledgeBase = "KnowledgeBase";
    public const string ContentGeneration = "ContentGeneration";
    public const string VideoRenders = "VideoRenders";

    /// <summary>Product knowledge: reading a tenant's documents and website, drafting answers from them, and any other AI step that builds the verified knowledge the chat answers from.</summary>
    public const string ProductKnowledge = "ProductKnowledge";

    /// <summary>"AI suggest" on the tenant's Domain keywords (onboarding and business profile).</summary>
    public const string BusinessKeywords = "BusinessKeywords";

    /// <summary>"AI suggest" on the lead discovery profile (Who to find): the search words used to find new customers. Separate from
    /// <see cref="DiscoveryLead"/>, which is the discovery agent that runs the searches.</summary>
    public const string DiscoveryKeywords = "DiscoveryKeywords";

    /// <summary>What answers customers in WhatsApp conversations. Stored in the platform AI settings (AiProviders:Provider and
    /// AiProviders:EmbeddingProvider), not under Integrations:Ai, because the conversation engine has always read them there.</summary>
    public const string Conversation = "Conversation";

    public static readonly IReadOnlyList<string> All = new[] { DiscoveryLead, KnowledgeBase, ContentGeneration, VideoRenders, ProductKnowledge, BusinessKeywords, DiscoveryKeywords, Conversation };

    /// <summary>Embeddings: Claude has no embeddings endpoint, so only ChatGPT and Gemini are offered for them.</summary>
    public static readonly IReadOnlyList<string> EmbeddingProviders = new[] { "OpenAI", "Google" };

    /// <summary>The third-party tools on offer: the provider id the AI clients know, shown as ChatGPT, Claude and Gemini.</summary>
    public static readonly IReadOnlyList<string> Providers = new[] { "OpenAI", "Anthropic", "Google" };

    /// <summary>
    /// The one place that says which third-party tools a feature really runs on - the page, the save validation and the feature itself all
    /// read this, so they cannot disagree. A provider appears here only when the feature's code can call it; a saved key does not make a
    /// provider supported. Adding a provider to one feature is a change to that feature's line only.
    ///   Discovery lead      - the discovery agent is Claude's (AnthropicLeadDiscoveryAgent), and its keyword suggestions use the same tool.
    ///   Discovery keywords  - the same text generator as business keywords, so all three work.
    ///   Business keywords   - AiTextGenerator calls ChatGPT, Claude or Gemini by name, so all three work.
    ///   Conversation        - AiServiceFactory has a client for ChatGPT, Claude and Gemini.
    ///   Knowledge base, content generation, video renders - nothing reads the choice yet, so no tool is offered until it is wired.
    /// </summary>
    public static IReadOnlyList<string> SupportedProviders(string feature) => feature switch
    {
        DiscoveryLead => new[] { "Anthropic" },
        Conversation => new[] { "OpenAI", "Anthropic", "Google" },
        ProductKnowledge => new[] { "OpenAI", "Anthropic", "Google" },
        BusinessKeywords => new[] { "Anthropic", "OpenAI", "Google" },
        DiscoveryKeywords => new[] { "Anthropic", "OpenAI", "Google" },
        _ => Array.Empty<string>(),
    };

    public static string Prefix(string feature) => $"Integrations:Ai:{feature}";

    /// <summary>What a feature does until the admin chooses. Discovery leads, business keywords and discovery keywords were always on a third-party tool, so those stay on; the rest start on the simulator.</summary>
    public static string DefaultMode(string feature) =>
        feature is DiscoveryLead or BusinessKeywords or DiscoveryKeywords ? PlatformIntegrationSettingsService.ApiMode : PlatformIntegrationSettingsService.SimulatorMode;
}

/// <summary>How a feature is handled. The key is the one already saved under Configuration > AI Providers - this only says whether one is there.</summary>
/// <param name="SupportedProviders">The tools this feature can really run on - the only ones the page offers and the save accepts. Empty means
/// the feature does not use a third-party tool yet.</param>
/// <param name="SavedProviderSupported">False when a tool saved earlier is no longer supported: it is kept as saved, not used, and the
/// simulator answers until the admin picks a supported one.</param>
/// <param name="ProviderEnabled">False when the tool is switched Off under Configuration > AI Providers: the choice is kept as saved, not used,
/// and the simulator answers until it is switched On again.</param>
public record AiFeatureChannelDto(
    string Mode, string Provider, bool ProviderHasKey, bool IsLive,
    IReadOnlyList<string> SupportedProviders, bool SavedProviderSupported, bool ProviderEnabled = true);

/// <summary>The conversation engine: which tool answers customers, and which embeds the knowledge base text it searches. Each is the simulator
/// or a third-party tool, and its key is the one saved under Configuration > AI Providers.</summary>
public record AiConversationDto(
    string Mode, string Provider, bool ProviderHasKey,
    string EmbeddingMode, string EmbeddingProvider, bool EmbeddingProviderHasKey,
    bool IsLive,
    IReadOnlyList<string> SupportedProviders, IReadOnlyList<string> SupportedEmbeddingProviders);

public record PlatformAiFeatureSettingsDto(
    AiFeatureChannelDto DiscoveryLead,
    AiFeatureChannelDto KnowledgeBase,
    AiFeatureChannelDto ContentGeneration,
    AiFeatureChannelDto VideoRenders,
    AiConversationDto Conversation,
    AiFeatureChannelDto BusinessKeywords,
    AiFeatureChannelDto DiscoveryKeywords,
    AiFeatureChannelDto ProductKnowledge);

/// <param name="Mode">"Simulator" or "Api".</param>
/// <param name="Provider">"OpenAI" (ChatGPT), "Anthropic" (Claude) or "Google" (Gemini).</param>
public record AiFeatureChannelRequest(string Mode, string Provider);

public record AiConversationRequest(string Mode, string Provider, string EmbeddingMode, string EmbeddingProvider);

/// <param name="Conversation">null leaves the conversation engine as it is.</param>
/// <param name="BusinessKeywords">null leaves the domain keyword suggestions as they are.</param>
/// <param name="DiscoveryKeywords">null leaves the discovery lead "AI suggest" as it is.</param>
public record UpdatePlatformAiFeatureSettingsRequest(
    AiFeatureChannelRequest DiscoveryLead,
    AiFeatureChannelRequest KnowledgeBase,
    AiFeatureChannelRequest ContentGeneration,
    AiFeatureChannelRequest VideoRenders,
    AiConversationRequest? Conversation = null,
    AiFeatureChannelRequest? BusinessKeywords = null,
    AiFeatureChannelRequest? DiscoveryKeywords = null,
    AiFeatureChannelRequest? ProductKnowledge = null);

public record TestAiFeatureRequest(AiFeatureChannelRequest Settings);

/// <summary>What a feature runs on, read at request time. Third-party means the named provider, with its saved key.</summary>
public record AiFeatureChoice(bool UsesThirdParty, string Provider);

public interface IAiFeatureRouting
{
    AiFeatureChoice Resolve(string feature);
}

public interface IPlatformAiFeatureSettingsService
{
    Task<PlatformAiFeatureSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    Task<PlatformAiFeatureSettingsDto> UpdateAsync(UpdatePlatformAiFeatureSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Asks the chosen provider one trivial question with the saved key. Always a result, never a throw for a provider problem.</summary>
    Task<DeliveryTestResultDto> TestAsync(string feature, TestAiFeatureRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Per AI feature (discovery leads, knowledge base, content generation, video renders): the built-in simulator, or a third-party tool -
/// ChatGPT, Claude or Gemini. Lives only in the AppSettings table, next to the other integrations, and applies from the next request.
/// Keys are not stored here again: they are the ones saved under Configuration > AI Providers, so a feature cannot be switched to a
/// provider that has no key.
/// </summary>
public class PlatformAiFeatureSettingsService : IPlatformAiFeatureSettingsService
{
    private readonly IAppSettingsStore _store;
    private readonly IPlatformAiConfigProvider _credentials;
    private readonly IAiTextGenerator _ai;

    public PlatformAiFeatureSettingsService(IAppSettingsStore store, IPlatformAiConfigProvider credentials, IAiTextGenerator ai)
    {
        _store = store;
        _credentials = credentials;
        _ai = ai;
    }

    public async Task<PlatformAiFeatureSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        var credentials = _credentials.Get();
        AiFeatureChannelDto For(string feature) => ToDto(stored, feature, credentials);
        return new PlatformAiFeatureSettingsDto(
            For(AiFeatures.DiscoveryLead), For(AiFeatures.KnowledgeBase), For(AiFeatures.ContentGeneration), For(AiFeatures.VideoRenders),
            ConversationDto(credentials), For(AiFeatures.BusinessKeywords), For(AiFeatures.DiscoveryKeywords), For(AiFeatures.ProductKnowledge));
    }

    public async Task<PlatformAiFeatureSettingsDto> UpdateAsync(UpdatePlatformAiFeatureSettingsRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        var credentials = _credentials.Get();
        var errors = new List<FluentValidation.Results.ValidationFailure>();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        void Collect(string feature, AiFeatureChannelRequest? channel)
        {
            if (channel is null)
            {
                errors.Add(new FluentValidation.Results.ValidationFailure(feature, "Choose how this works."));
                return;
            }

            var problem = Validate(feature, channel, credentials);
            if (problem is not null)
            {
                errors.Add(new FluentValidation.Results.ValidationFailure($"{feature}.{problem.Value.Property}", problem.Value.Message));
                return;
            }

            values[AiFeatures.Prefix(feature) + ":Mode"] = IsApi(channel.Mode) ? PlatformIntegrationSettingsService.ApiMode : PlatformIntegrationSettingsService.SimulatorMode;
            // On the simulator the tool is irrelevant: what was saved before stays, so nothing the admin chose earlier is lost.
            if (IsApi(channel.Mode))
                values[AiFeatures.Prefix(feature) + ":Provider"] = Canonical(channel.Provider)!;
        }

        Collect(AiFeatures.DiscoveryLead, request.DiscoveryLead);
        Collect(AiFeatures.KnowledgeBase, request.KnowledgeBase);
        Collect(AiFeatures.ContentGeneration, request.ContentGeneration);
        Collect(AiFeatures.VideoRenders, request.VideoRenders);
        if (request.BusinessKeywords is not null)
            Collect(AiFeatures.BusinessKeywords, request.BusinessKeywords);
        if (request.ProductKnowledge is not null)
            Collect(AiFeatures.ProductKnowledge, request.ProductKnowledge);
        if (request.DiscoveryKeywords is not null)
            Collect(AiFeatures.DiscoveryKeywords, request.DiscoveryKeywords);

        if (request.Conversation is { } conversation)
        {
            var problem = ValidateConversation(conversation, credentials);
            if (problem is not null)
                errors.Add(new FluentValidation.Results.ValidationFailure($"Conversation.{problem.Value.Property}", problem.Value.Message));
            else
            {
                // The simulator is stored the way the conversation engine has always read it: the word "Simulated".
                values["AiProviders:Provider"] = IsApi(conversation.Mode) ? Canonical(conversation.Provider)! : "Simulated";
                values["AiProviders:EmbeddingProvider"] = IsApi(conversation.EmbeddingMode) ? Canonical(conversation.EmbeddingProvider)! : "Simulated";
            }
        }

        if (errors.Count > 0)
            throw new ValidationException(errors);

        await _store.UpsertAsync(values, updatedByUserId, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task<DeliveryTestResultDto> TestAsync(string feature, TestAiFeatureRequest request, CancellationToken cancellationToken = default)
    {
        if (!AiFeatures.All.Contains(feature, StringComparer.OrdinalIgnoreCase))
            throw new ValidationException(new[] { new FluentValidation.Results.ValidationFailure("Feature", "Unknown feature.") });

        if (!IsApi(request.Settings.Mode))
            return new DeliveryTestResultDto(true, "The simulator is selected, so nothing is sent anywhere. Switch to a third-party tool to test a real call.");

        var problem = Validate(AiFeatures.All.First(f => string.Equals(f, feature, StringComparison.OrdinalIgnoreCase)), request.Settings, _credentials.Get());
        if (problem is not null)
            throw new ValidationException(new[] { new FluentValidation.Results.ValidationFailure($"Settings.{problem.Value.Property}", problem.Value.Message) });

        var provider = Canonical(request.Settings.Provider)!;
        var reply = await _ai.GenerateAsync("Reply with the single word OK.", "Say OK.", 20, cancellationToken, provider);
        var name = DisplayName(provider);

        return string.IsNullOrWhiteSpace(reply)
            ? new DeliveryTestResultDto(false, $"{name} did not answer. Check the {name} key and model under Configuration > AI Providers.")
            : new DeliveryTestResultDto(true, $"{name} answered, so this feature can use it.");
    }

    /// <summary>The first thing wrong with a channel, or null. Switching to a provider with no saved key would make every call fail.</summary>
    private static (string Property, string Message)? Validate(string feature, AiFeatureChannelRequest channel, AiCredentials credentials)
    {
        var mode = channel.Mode?.Trim() ?? string.Empty;
        if (!IsApi(mode) && !string.Equals(mode, PlatformIntegrationSettingsService.SimulatorMode, StringComparison.OrdinalIgnoreCase))
            return ("Mode", "Choose Simulator or a third-party tool.");

        // The simulator needs no tool, so a leftover or unsupported provider value is harmless there.
        if (!IsApi(mode))
            return null;

        var supported = AiFeatures.SupportedProviders(feature);
        if (supported.Count == 0)
            return ("Mode", "This feature does not use a third-party tool yet, so only the simulator is available.");

        var provider = Canonical(channel.Provider);
        if (provider is null)
            return ("Provider", "Choose one of the tools listed for this feature.");

        if (!supported.Contains(provider))
            return ("Provider", $"{DisplayName(provider)} is not available for this feature. Available: {string.Join(", ", supported.Select(DisplayName))}.");

        if (!credentials.IsEnabled(provider))
            return ("Provider", $"{DisplayName(provider)} is turned off. Switch it on under Configuration > AI Providers first.");

        if (!HasKey(credentials, provider))
            return ("Provider", $"No {DisplayName(provider)} key is saved yet. Add it under Configuration > AI Providers first.");

        return null;
    }

    private static AiConversationDto ConversationDto(AiCredentials credentials)
    {
        // What the conversation engine really runs on right now, so the page can never disagree with it.
        var provider = Canonical(credentials.Provider);
        var embedding = Canonical(credentials.EmbeddingProvider);
        var providerKey = provider is not null && HasKey(credentials, provider);
        var embeddingKey = embedding is not null && HasKey(credentials, embedding);
        return new AiConversationDto(
            provider is null ? PlatformIntegrationSettingsService.SimulatorMode : PlatformIntegrationSettingsService.ApiMode,
            provider ?? AiFeatures.Providers[0], providerKey,
            embedding is null ? PlatformIntegrationSettingsService.SimulatorMode : PlatformIntegrationSettingsService.ApiMode,
            embedding ?? AiFeatures.EmbeddingProviders[0], embeddingKey,
            provider is not null && providerKey,
            AiFeatures.SupportedProviders(AiFeatures.Conversation), AiFeatures.EmbeddingProviders);
    }

    private static (string Property, string Message)? ValidateConversation(AiConversationRequest c, AiCredentials credentials)
    {
        var chat = Validate(AiFeatures.Conversation, new AiFeatureChannelRequest(c.Mode, c.Provider), credentials);
        if (chat is not null)
            return chat;

        if (!IsApi(c.EmbeddingMode))
        {
            return string.Equals(c.EmbeddingMode?.Trim(), PlatformIntegrationSettingsService.SimulatorMode, StringComparison.OrdinalIgnoreCase)
                ? null
                : ("EmbeddingMode", "Choose Simulator or a third-party tool.");
        }

        var embeddingProvider = Canonical(c.EmbeddingProvider);
        if (embeddingProvider is null || !AiFeatures.EmbeddingProviders.Contains(embeddingProvider))
            return ("EmbeddingProvider", "Choose ChatGPT or Gemini for embeddings - Claude does not offer them.");
        if (!HasKey(credentials, embeddingProvider))
            return ("EmbeddingProvider", $"No {DisplayName(embeddingProvider)} key is saved yet. Add it under Configuration > AI Providers first.");
        return null;
    }

    private static AiFeatureChannelDto ToDto(IReadOnlyDictionary<string, string?> stored, string feature, AiCredentials credentials)
    {
        var savedMode = Get(stored, AiFeatures.Prefix(feature) + ":Mode");
        var mode = IsApi(savedMode.Length == 0 ? AiFeatures.DefaultMode(feature) : savedMode)
            ? PlatformIntegrationSettingsService.ApiMode
            : PlatformIntegrationSettingsService.SimulatorMode;
        var supported = AiFeatures.SupportedProviders(feature);
        var savedProvider = Canonical(Get(stored, AiFeatures.Prefix(feature) + ":Provider"));
        var provider = savedProvider ?? supported.FirstOrDefault() ?? AiFeatures.Providers[0];
        // Nothing saved is fine (the first supported tool is shown); a saved tool the feature cannot run on is reported, never rewritten.
        var providerSupported = supported.Contains(provider);
        var hasKey = HasKey(credentials, provider);
        return new AiFeatureChannelDto(
            mode, provider, hasKey, mode == PlatformIntegrationSettingsService.ApiMode && hasKey && providerSupported, supported, savedProvider is null || providerSupported,
            credentials.IsEnabled(provider));
    }

    public static bool HasKey(AiCredentials credentials, string provider) => provider switch
    {
        "OpenAI" => !string.IsNullOrWhiteSpace(credentials.OpenAiApiKey),
        "Anthropic" => !string.IsNullOrWhiteSpace(credentials.AnthropicApiKey),
        "Google" => !string.IsNullOrWhiteSpace(credentials.GoogleApiKey),
        _ => false,
    };

    public static string? Canonical(string? provider) =>
        AiFeatures.Providers.FirstOrDefault(p => string.Equals(p, provider?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(string provider) => provider switch
    {
        "OpenAI" => "ChatGPT",
        "Anthropic" => "Claude",
        "Google" => "Gemini",
        _ => provider,
    };

    private static bool IsApi(string? mode) => string.Equals(mode?.Trim(), PlatformIntegrationSettingsService.ApiMode, StringComparison.OrdinalIgnoreCase);

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
}
