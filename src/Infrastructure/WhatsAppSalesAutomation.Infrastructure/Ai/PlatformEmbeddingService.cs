using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// The PLATFORM's own embedder: the provider, key and model in the platform-wide <c>AiProviders</c>
/// settings (the ones a PlatformSuperAdmin edits on the Configuration screen), used regardless of which
/// tenant - if any - is in scope.
///
/// Why it exists separately from <see cref="TenantEmbeddingService"/>: that one resolves credentials
/// per tenant, and a tenant with no configuration falls back to the Simulated stand-in. Platform-authored
/// knowledge (refund policy, how credits work) belongs to nobody, so indexing it has no tenant to borrow
/// credentials from - and embedding it with the fake provider would put it in a vector space that no
/// real tenant's questions can ever match, silently. Everything about platform knowledge that needs an
/// embedding goes through here, so the platform pays for it and it lands in one known space.
///
/// Cost model: the platform pays to embed each platform article once, and a small amount per tenant
/// question to embed that question in the platform's space so it can be matched against them.
/// </summary>
public sealed class PlatformEmbeddingService : IPlatformEmbeddingService
{
    private readonly IOptionsSnapshot<AiProviderSettings> _settings;
    private readonly OpenAiEmbeddingClient _openAi;
    private readonly GoogleEmbeddingClient _google;
    private readonly SimulatedEmbeddingClient _simulated;

    public PlatformEmbeddingService(
        IOptionsSnapshot<AiProviderSettings> settings,
        OpenAiEmbeddingClient openAi,
        GoogleEmbeddingClient google,
        SimulatedEmbeddingClient simulated)
    {
        _settings = settings;
        _openAi = openAi;
        _google = google;
        _simulated = simulated;
    }

    private AiProviderSettings Settings => _settings.Value;

    /// <summary>What the platform is configured to use, falling back to Simulated only when the chosen
    /// real provider has no key - the same "not configured is as available as Simulated" rule as the
    /// per-tenant service, so a fresh install still runs.</summary>
    private string Effective => Settings.EmbeddingProvider?.ToLowerInvariant() switch
    {
        "openai" when !string.IsNullOrWhiteSpace(Settings.OpenAI.ApiKey) => "openai",
        "google" when !string.IsNullOrWhiteSpace(Settings.Google.ApiKey) => "google",
        _ => "simulated"
    };

    public string ProviderName => Effective switch { "openai" => "OpenAI", "google" => "Google", _ => "Simulated" };

    public string ModelName => Effective switch
    {
        "openai" => Settings.OpenAI.EmbeddingModel,
        "google" => Settings.Google.EmbeddingModel,
        _ => _simulated.ModelName
    };

    public bool IsAvailable => true;

    /// <summary>True when platform knowledge is being embedded by the stand-in provider, i.e. it will
    /// not be found by tenants using a real one. Surfaced on the platform screen as a warning.</summary>
    public bool IsSimulated => Effective == "simulated";

    /// <summary>The setting asked for a real provider but its key is missing - distinct from choosing
    /// Simulated on purpose, and the more likely thing to be a mistake.</summary>
    public bool RealProviderRequestedButUnconfigured =>
        Effective == "simulated" &&
        Settings.EmbeddingProvider is { } p &&
        !string.Equals(p, "Simulated", StringComparison.OrdinalIgnoreCase);

    public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        return Effective switch
        {
            "openai" => _openAi.GetEmbeddingAsync(Credentials(), text, cancellationToken),
            "google" => _google.GetEmbeddingAsync(Credentials(), text, cancellationToken),
            _ => _simulated.GetEmbeddingAsync(text, cancellationToken)
        };
    }

    /// <summary>The existing embedding clients take the per-tenant credentials shape, so the platform's
    /// settings are presented in it. Only the embedding fields are ever read.</summary>
    private TenantAiCredentials Credentials() => new(
        Provider: Settings.Provider,
        EmbeddingProvider: Settings.EmbeddingProvider,
        AnthropicApiKey: Settings.Anthropic.ApiKey,
        AnthropicModel: Settings.Anthropic.Model,
        AnthropicApiVersion: Settings.Anthropic.ApiVersion,
        AnthropicBaseUrl: Settings.Anthropic.BaseUrl,
        OpenAiApiKey: Settings.OpenAI.ApiKey,
        OpenAiChatModel: Settings.OpenAI.ChatModel,
        OpenAiEmbeddingModel: Settings.OpenAI.EmbeddingModel,
        OpenAiBaseUrl: Settings.OpenAI.BaseUrl,
        GoogleApiKey: Settings.Google.ApiKey,
        GoogleChatModel: Settings.Google.ChatModel,
        GoogleEmbeddingModel: Settings.Google.EmbeddingModel,
        GoogleBaseUrl: Settings.Google.BaseUrl);
}
