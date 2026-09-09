using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// The DI-registered <see cref="IEmbeddingProviderCatalog"/> - every provider the current tenant could
/// call, regardless of which one is "active" (<see cref="TenantEmbeddingService"/> is that single one) -
/// see IEmbeddingProviderCatalog's own doc comment for why KnowledgeBaseService.ReembedAsync needs both.
/// Always returns all three providers (Simulated/OpenAI/Google), same as the pre-multi-tenant
/// EmbeddingProviderCatalog did regardless of which single one AiProviderSettings.EmbeddingProvider
/// named - IsAvailable, not list membership, is what gates whether OpenAI/Google are actually usable
/// for a tenant that has not configured (or not fully configured) them.
/// </summary>
public class TenantEmbeddingProviderCatalog : IEmbeddingProviderCatalog
{
    private readonly ITenantAiConfigProvider _configProvider;
    private readonly OpenAiEmbeddingClient _openAiClient;
    private readonly GoogleEmbeddingClient _googleClient;
    private readonly SimulatedEmbeddingClient _simulatedClient;

    public TenantEmbeddingProviderCatalog(
        ITenantAiConfigProvider configProvider,
        OpenAiEmbeddingClient openAiClient,
        GoogleEmbeddingClient googleClient,
        SimulatedEmbeddingClient simulatedClient)
    {
        _configProvider = configProvider;
        _openAiClient = openAiClient;
        _googleClient = googleClient;
        _simulatedClient = simulatedClient;
    }

    // Blocking, memoized via ITenantAiConfigProvider's own per-scope cache - see
    // TenantEmbeddingService's identical property/doc comment for why this is safe and cheap.
    public IReadOnlyList<IEmbeddingService> AllProviders
    {
        get
        {
            var credentials = _configProvider.GetForCurrentTenantAsync().GetAwaiter().GetResult();
            return new IEmbeddingService[]
            {
                _simulatedClient,
                new OpenAiAdapter(_openAiClient, credentials),
                new GoogleAdapter(_googleClient, credentials)
            };
        }
    }

    /// <summary>Binds OpenAiEmbeddingClient to one already-resolved tenant's credentials so it can be
    /// handed out as a plain IEmbeddingService list member - see TenantEmbeddingService's doc comment
    /// for why credentials has to be resolved once, up front, rather than per interface call.</summary>
    private class OpenAiAdapter : IEmbeddingService
    {
        private readonly OpenAiEmbeddingClient _client;
        private readonly TenantAiCredentials? _credentials;

        public OpenAiAdapter(OpenAiEmbeddingClient client, TenantAiCredentials? credentials)
        {
            _client = client;
            _credentials = credentials;
        }

        public string ProviderName => "OpenAI";

        public string ModelName => _credentials?.OpenAiEmbeddingModel ?? string.Empty;

        public bool IsAvailable => !string.IsNullOrWhiteSpace(_credentials?.OpenAiApiKey);

        public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            _credentials is null ? Task.FromResult(Array.Empty<float>()) : _client.GetEmbeddingAsync(_credentials, text, cancellationToken);
    }

    private class GoogleAdapter : IEmbeddingService
    {
        private readonly GoogleEmbeddingClient _client;
        private readonly TenantAiCredentials? _credentials;

        public GoogleAdapter(GoogleEmbeddingClient client, TenantAiCredentials? credentials)
        {
            _client = client;
            _credentials = credentials;
        }

        public string ProviderName => "Google";

        public string ModelName => _credentials?.GoogleEmbeddingModel ?? string.Empty;

        public bool IsAvailable => !string.IsNullOrWhiteSpace(_credentials?.GoogleApiKey);

        public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            _credentials is null ? Task.FromResult(Array.Empty<float>()) : _client.GetEmbeddingAsync(_credentials, text, cancellationToken);
    }
}
