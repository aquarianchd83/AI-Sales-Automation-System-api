using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The models a provider can be run on, so a screen can offer a dropdown of them instead of a text box. Read from the platform's own model
/// catalogue (AI Provider Models), not from the provider's API.</summary>
public interface IProviderModelCatalog
{
    /// <param name="provider">"Anthropic", "OpenAI" or "Google".</param>
    /// <returns>The active chat models the catalogue holds for the provider. Empty when it holds none - the page then offers the priced models and
    /// the one in use.</returns>
    Task<IReadOnlyList<string>> ListAsync(string provider, CancellationToken cancellationToken = default);

    /// <returns>The active models of the given type (chat, embedding, image, speech, video) the catalogue holds for the provider.</returns>
    Task<IReadOnlyList<string>> ListAsync(string provider, AiModelType type, CancellationToken cancellationToken = default);
}
