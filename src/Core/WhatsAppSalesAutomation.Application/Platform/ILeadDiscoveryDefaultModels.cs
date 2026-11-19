namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The model lead discovery runs on for a provider when none is chosen under "Model by task": the server's discovery defaults
/// (<c>LeadDiscovery:Agent</c>), which are deliberately not the chat models.</summary>
public interface ILeadDiscoveryDefaultModels
{
    /// <param name="provider">"Anthropic", "OpenAI" or "Google".</param>
    string ModelFor(string provider);
}
