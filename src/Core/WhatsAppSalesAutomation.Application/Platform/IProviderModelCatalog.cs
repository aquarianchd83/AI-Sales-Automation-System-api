namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The models a provider offers, as the provider itself lists them with the platform's saved key, so "Model by task" can offer every one of
/// them and not only the ones that already have a price.</summary>
public interface IProviderModelCatalog
{
    /// <param name="provider">"Anthropic", "OpenAI" or "Google".</param>
    /// <returns>The text-generation models the provider lists, in no particular order. Empty when the provider is off, has no key or cannot be reached
    /// - the page then offers the priced models and the one in use, as before.</returns>
    Task<IReadOnlyList<string>> ListAsync(string provider, CancellationToken cancellationToken = default);
}
