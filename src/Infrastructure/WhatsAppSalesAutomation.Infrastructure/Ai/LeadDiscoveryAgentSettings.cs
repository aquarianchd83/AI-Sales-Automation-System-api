namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Bound from "LeadDiscovery:Agent". Lead discovery has its own credentials, deliberately separate from
/// "AiProviders" and from each tenant's own AI provider config: the tenants' keys pay for their WhatsApp
/// replies, while discovery research is a platform-run cost on one key, billed to the platform's own
/// Anthropic Console account.
///
/// Keep <see cref="ApiKey"/> out of appsettings.json, which is in source control. Supply it through the
/// environment variable <c>LeadDiscovery__Agent__ApiKey</c>, user secrets, or whatever the deployment uses
/// for secrets.
/// </summary>
/// <remarks>
/// The model each engine researches with is not here: it is chosen and saved on the AI Providers page (Model by task > Lead discovery,
/// <c>AiProviders:TaskModels:LeadDiscovery:{Provider}</c>), so there is no model default in code.
/// </remarks>
public class LeadDiscoveryAgentSettings
{
    /// <summary>"Anthropic" (default), "OpenAI" (ChatGPT) or "Simulated" - the same convention as WhatsAppSettings.Provider.
    /// "Simulated" runs the whole pipeline against invented businesses without calling any API or spending
    /// anything, for testing. See SimulatedLeadDiscoveryAgent.</summary>
    public string Provider { get; set; } = "Anthropic";

    /// <summary>Used when <see cref="Provider"/> is "OpenAI": the key defaults to the OpenAI key saved under
    /// Configuration > AI Providers, so set this only for a dedicated discovery key.</summary>
    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>Empty uses the base URL saved for OpenAI under Configuration > AI Providers.</summary>
    public string OpenAiBaseUrl { get; set; } = string.Empty;

    /// <summary>"low"/"medium"/"high", or empty to use the API default. Web search needs at least "low".</summary>
    public string? OpenAiReasoningEffort { get; set; } = "low";

    /// <summary>How much of a fetched page, in characters, the OpenAI agent reads (and is verified against).</summary>
    public int OpenAiMaxFetchChars { get; set; } = 20000;

    /// <summary>Used when the engine is Gemini: the key defaults to the Google key saved under Configuration > AI Providers, so set this only
    /// for a dedicated discovery key.</summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    /// <summary>The platform's own Anthropic API key. Empty means lead discovery is not configured, and every
    /// tenant's run is skipped rather than failed.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Not a secret - configurable for a regional endpoint or a mock server, as
    /// AnthropicSettings.BaseUrl is.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1/";

    public string ApiVersion { get; set; } = "2023-06-01";

    public int MaxTokens { get; set; } = 16000;

    /// <summary>"low"/"medium"/"high"/"xhigh"/"max", or empty to use the API default. Must stay empty for
    /// Claude Haiku 4.5, which rejects an effort setting.</summary>
    public string? Effort { get; set; }

    /// <summary>web_search max_uses for one round.</summary>
    public int MaxSearchesPerRound { get; set; } = 15;

    /// <summary>web_fetch max_uses for one round.</summary>
    public int MaxFetchesPerRound { get; set; } = 25;

    /// <summary>web_fetch max_content_tokens - a contact page fits easily; a very long page is truncated.</summary>
    public int MaxFetchContentTokens { get; set; } = 10000;

    /// <summary>The most requests one round makes, counting pause_turn continuations and the final nudge
    /// to submit.</summary>
    public int MaxTurns { get; set; } = 6;

    /// <summary>One request can run many searches and fetches server-side before it responds.</summary>
    public int RequestTimeoutMinutes { get; set; } = 10;
}
