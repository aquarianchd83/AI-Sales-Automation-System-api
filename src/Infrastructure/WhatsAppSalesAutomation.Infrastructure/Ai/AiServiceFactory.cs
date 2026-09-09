using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// The DI-registered <see cref="IAiService"/> - see <see cref="WhatsApp.WhatsAppServiceFactory"/>'s own
/// doc comment for the identical reasoning (provider *type* selection can no longer be one DI-time
/// choice once each tenant configures independently; this factory replaces
/// DependencyInjection.AddAiClients's old startup-time switch for chat). Falls back to
/// <see cref="SimulatedAiClient"/> - never throws - whenever the current tenant has no AI config row,
/// or has one but left <see cref="TenantAiCredentials.Provider"/> as "Simulated" (the default), or
/// picked a real provider without supplying that provider's API key.
/// </summary>
public class AiServiceFactory : IAiService
{
    private readonly ITenantAiConfigProvider _configProvider;
    private readonly AnthropicAiClient _anthropicClient;
    private readonly OpenAiAiClient _openAiClient;
    private readonly GoogleAiClient _googleClient;
    private readonly SimulatedAiClient _simulatedClient;

    public AiServiceFactory(
        ITenantAiConfigProvider configProvider,
        AnthropicAiClient anthropicClient,
        OpenAiAiClient openAiClient,
        GoogleAiClient googleClient,
        SimulatedAiClient simulatedClient)
    {
        _configProvider = configProvider;
        _anthropicClient = anthropicClient;
        _openAiClient = openAiClient;
        _googleClient = googleClient;
        _simulatedClient = simulatedClient;
    }

    public async Task<AiReplyResult> GetResponseAsync(AiConversationContext context, CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);

        if (credentials is null)
            return await _simulatedClient.GetResponseAsync(context, cancellationToken);

        return credentials.Provider.ToLowerInvariant() switch
        {
            "anthropic" when !string.IsNullOrWhiteSpace(credentials.AnthropicApiKey)
                => await _anthropicClient.GetResponseAsync(credentials, context, cancellationToken),
            "openai" when !string.IsNullOrWhiteSpace(credentials.OpenAiApiKey)
                => await _openAiClient.GetResponseAsync(credentials, context, cancellationToken),
            "google" when !string.IsNullOrWhiteSpace(credentials.GoogleApiKey)
                => await _googleClient.GetResponseAsync(credentials, context, cancellationToken),
            // Either genuinely "Simulated", or a real provider selected without its API key - the
            // latter should never silently look "fine", but falling all the way back to a hard
            // failure here would take down the whole inbound-message pipeline for a config mistake
            // that TenantSettingsController could instead just warn about at save time.
            _ => await _simulatedClient.GetResponseAsync(context, cancellationToken)
        };
    }
}
