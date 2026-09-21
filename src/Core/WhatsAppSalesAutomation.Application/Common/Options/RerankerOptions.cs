namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from "Reranker:Cohere". The cross-encoder that judges whether a passage ANSWERS a question
/// (§L). Not in <c>AppSettingCatalog</c>: it holds an API key, and - like RateLimitOptions - this is
/// deployment configuration rather than something a tenant admin adjusts.
///
/// With no ApiKey the reranker is simply unconfigured, and retrieval runs in fusion-only mode with its
/// stricter evidence gate. That is a supported state, not an error: the agent escalates more, it does
/// not guess more.
/// </summary>
public class RerankerOptions
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Cohere's multilingual rerank model, which handles Hindi (§L.2).</summary>
    public string Model { get; set; } = "rerank-v3.5";

    public string BaseUrl { get; set; } = "https://api.cohere.com/";

    /// <summary>Per attempt. Reranking sits on the request path of a ticket reply, and a reranker
    /// that answers in ten seconds is one that has effectively failed - better to fall back.</summary>
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>Consecutive failures before the circuit opens.</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    public int CircuitOpenSeconds { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
