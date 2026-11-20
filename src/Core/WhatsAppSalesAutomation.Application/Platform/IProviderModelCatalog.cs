namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The models a provider can be run on, so "Model by task" can offer every one of them and not only the ones that already have a price.
/// Read from the platform's own model catalogue (AI Provider Models), not from the provider's API.</summary>
public interface IProviderModelCatalog
{
    /// <param name="provider">"Anthropic", "OpenAI" or "Google".</param>
    /// <returns>The active chat models the catalogue holds for the provider. Empty when it holds none - the page then offers the priced models and
    /// the one in use.</returns>
    Task<IReadOnlyList<string>> ListAsync(string provider, CancellationToken cancellationToken = default);
}
