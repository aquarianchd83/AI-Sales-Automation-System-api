using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Infrastructure.Tenancy;

/// <summary>
/// One tenant's own AI provider choice and API keys - 1:1 with Tenant, keyed directly by
/// <see cref="TenantId"/>, mirroring today's (pre-multi-tenant) global <c>AiProviderSettings</c>
/// shape field-for-field. Same "no row = not configured, defaults to Simulated" and
/// Infrastructure-internal treatment as <see cref="TenantWhatsAppConfig"/> - see that class's own
/// doc comment for both.
/// </summary>
public class TenantAiProviderConfig : ITenantOwned
{
    /// <summary>Doubles as the primary key - true 1:1 with Tenant, not just a unique index.</summary>
    public Guid TenantId { get; set; }

    /// <summary>"Simulated" (default), "Anthropic", "OpenAI", or "Google" - see AiProviderSettings.Provider's
    /// own doc comment for the reasoning; identical here, just per-tenant now.</summary>
    public string Provider { get; set; } = "Simulated";

    /// <summary>"Simulated" (default), "OpenAI", or "Google" - independent of <see cref="Provider"/>,
    /// see AiProviderSettings.EmbeddingProvider's own doc comment.</summary>
    public string EmbeddingProvider { get; set; } = "Simulated";

    /// <summary>Ciphertext.</summary>
    public string? AnthropicApiKey { get; set; }

    public string AnthropicModel { get; set; } = "claude-haiku-4-5-20251001";

    public string AnthropicApiVersion { get; set; } = "2023-06-01";

    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com/v1/";

    /// <summary>Ciphertext.</summary>
    public string? OpenAiApiKey { get; set; }

    public string OpenAiChatModel { get; set; } = "gpt-5-nano";

    public string OpenAiEmbeddingModel { get; set; } = "text-embedding-3-small";

    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1/";

    /// <summary>Ciphertext.</summary>
    public string? GoogleApiKey { get; set; }

    public string GoogleChatModel { get; set; } = "gemini-flash-lite-latest";

    public string GoogleEmbeddingModel { get; set; } = "text-embedding-004";

    public string GoogleBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";

    public DateTime UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
