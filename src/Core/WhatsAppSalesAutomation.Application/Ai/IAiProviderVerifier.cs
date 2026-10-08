namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>Checks the platform's saved AI provider keys against each provider, without spending tokens.</summary>
public interface IAiProviderVerifier
{
    /// <summary>One result per provider (Anthropic, OpenAI, Google), in that order - or only <paramref name="provider"/>'s when given. Never throws: a provider that cannot be
    /// reached or refuses the key comes back as a failed result with the reason.</summary>
    Task<IReadOnlyList<AiProviderCheckDto>> VerifyAsync(string? provider, CancellationToken cancellationToken);
}

/// <param name="Provider">"Anthropic", "OpenAI" or "Google".</param>
/// <param name="Configured">False when no API key is saved for it (nothing was tried).</param>
/// <param name="IsActive">True for the provider chat replies currently use, or the one embeddings use.</param>
/// <param name="Success">True when the provider accepted the key.</param>
public record AiProviderCheckDto(string Provider, bool Configured, bool IsActive, bool Success, string Message);
