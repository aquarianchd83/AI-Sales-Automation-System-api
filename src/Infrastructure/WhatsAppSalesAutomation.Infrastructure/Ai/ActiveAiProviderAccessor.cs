using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Tenant-aware implementation of <see cref="IActiveAiProviderAccessor"/> - see
/// IPlatformAiConfigProvider's own doc comment for why this reads the current tenant's config instead
/// of one global AiProviderSettings now. Same blocking-on-a-memoized-task shape as
/// TenantEmbeddingService's sync properties, for the same reason (IActiveAiProviderAccessor.ActiveProvider
/// is a plain synchronous property every existing caller - KnowledgeBaseService - already reads that way).
/// </summary>
public class ActiveAiProviderAccessor : IActiveAiProviderAccessor
{
    private readonly IPlatformAiConfigProvider _configProvider;

    public ActiveAiProviderAccessor(IPlatformAiConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    private AiCredentials Credentials => _configProvider.Get();

    public string ActiveProvider => Credentials?.Provider ?? "Simulated";

    public bool HasApiKey(string provider)
    {
        var credentials = Credentials;
        if (credentials is null)
            return false;

        return provider?.ToLowerInvariant() switch
        {
            "anthropic" => !string.IsNullOrWhiteSpace(credentials.AnthropicApiKey),
            "openai" => !string.IsNullOrWhiteSpace(credentials.OpenAiApiKey),
            "google" => !string.IsNullOrWhiteSpace(credentials.GoogleApiKey),
            _ => false
        };
    }
}
