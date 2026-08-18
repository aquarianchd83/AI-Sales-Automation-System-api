namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Bound from the "AiProviders" config section. Kept separate from Application's AiOptions (the "Ai"
/// section - confidence threshold, escalation intents, RAG tuning) because this holds API keys and
/// model identifiers, which are Infrastructure-only concerns - the same split as WhatsAppSettings vs
/// MessagingOptions.
/// </summary>
public class AiProviderSettings
{
    /// <summary>
    /// "Simulated" (default, no API key needed - rule-based keyword matching, like
    /// WhatsAppSettings.Provider's own "Simulated" option), "Anthropic", "OpenAI", or "Google" - which
    /// IAiService implementation actually generates replies. Defaults to Simulated for the same reason
    /// the WhatsApp client does: the whole pipeline should be runnable and testable with zero external
    /// credentials. Of the three real options, Google's Gemini Flash-Lite tier is the cheapest per-token
    /// as of this codebase's knowledge cutoff (Jan 2026) - a reasonable first choice if/when this is
    /// switched to a real provider, though pricing changes and should be re-checked before relying on
    /// it for cost planning.
    /// </summary>
    public string Provider { get; set; } = "Simulated";

    /// <summary>"Simulated" (default), "OpenAI", or "Google" - which IEmbeddingService implementation
    /// generates vectors for RAG. Independent of <see cref="Provider"/>: Anthropic's Messages API has
    /// no embeddings endpoint, so a Claude-for-chat deployment still needs one of the other two (or
    /// Simulated) here.</summary>
    public string EmbeddingProvider { get; set; } = "Simulated";

    public AnthropicSettings Anthropic { get; set; } = new();

    public OpenAiSettings OpenAI { get; set; } = new();

    public GoogleAiSettings Google { get; set; } = new();

    /// <summary>0-100. Mirrors WhatsAppSettings.SimulatedFailureRatePercent - lets the escalation-on-
    /// send-failure path be exercised locally without needing a real provider outage.</summary>
    public int SimulatedFailureRatePercent { get; set; } = 0;
}

public class AnthropicSettings
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Anthropic's cheapest current model as of this codebase's knowledge cutoff (Jan 2026) -
    /// verify against Anthropic's model list before relying on this for production cost planning; model
    /// names and pricing tiers change over time.</summary>
    public string Model { get; set; } = "claude-haiku-4-5-20251001";

    public string ApiVersion { get; set; } = "2023-06-01";
}

public class OpenAiSettings
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>See AnthropicSettings.Model's doc comment - the same "verify before relying on this"
    /// caveat applies to every model default in this file.</summary>
    public string ChatModel { get; set; } = "gpt-5-nano";

    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
}

public class GoogleAiSettings
{
    public string ApiKey { get; set; } = string.Empty;

    public string ChatModel { get; set; } = "gemini-flash-lite-latest";

    public string EmbeddingModel { get; set; } = "text-embedding-004";
}
