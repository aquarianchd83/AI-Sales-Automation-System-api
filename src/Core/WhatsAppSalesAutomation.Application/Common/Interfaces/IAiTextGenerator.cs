namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// One-shot text generation with the calling tenant's own AI provider - for small helper features (suggesting
/// keywords, say) rather than customer conversations, which go through <see cref="IAiService"/>.
/// </summary>
public interface IAiTextGenerator
{
    /// <summary>
    /// The model's reply, or null when there is nothing real to ask: the tenant's provider is Simulated, or has no API
    /// key. Also null when the provider call fails - a helper feature has a fallback and must never break the screen.
    /// </summary>
    Task<string?> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default);
}
