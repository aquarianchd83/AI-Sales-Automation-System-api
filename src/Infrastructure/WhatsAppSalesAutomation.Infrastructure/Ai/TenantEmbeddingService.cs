using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// The DI-registered "active" <see cref="IEmbeddingService"/> - query-time retrieval
/// (KnowledgeBaseService.RetrieveRelevantChunksAsync) only ever needs this single tenant-configured
/// provider, unlike <see cref="TenantEmbeddingProviderCatalog"/> (used by ReembedAsync, which
/// deliberately embeds with every available provider at once). See
/// <see cref="WhatsApp.WhatsAppServiceFactory"/>'s own doc comment for the identical per-tenant-router
/// reasoning.
///
/// <see cref="ProviderName"/>/<see cref="ModelName"/>/<see cref="IsAvailable"/> are synchronous -
/// IEmbeddingService predates multi-tenancy and every existing caller reads them as plain properties,
/// so changing that interface would ripple into KnowledgeBaseService for no benefit here. Resolving the
/// tenant's AI config is an async DB read, so these properties block on
/// ITenantAiConfigProvider.GetForCurrentTenantAsync's own per-scope-memoized task - the first property
/// or GetEmbeddingAsync call in a scope pays one real DB round trip, every access after that (sync or
/// async) is instant. Blocking is safe here (ASP.NET Core/Hangfire have no SynchronizationContext to
/// deadlock against) but is still a known wart, not the ideal shape - a fully async IEmbeddingService
/// would be cleaner if this interface is ever revisited.
/// </summary>
public class TenantEmbeddingService : IEmbeddingService
{
    private readonly ITenantAiConfigProvider _configProvider;
    private readonly OpenAiEmbeddingClient _openAiClient;
    private readonly GoogleEmbeddingClient _googleClient;
    private readonly SimulatedEmbeddingClient _simulatedClient;

    public TenantEmbeddingService(
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

    private TenantAiCredentials? Credentials => _configProvider.GetForCurrentTenantAsync().GetAwaiter().GetResult();

    public string ProviderName => Credentials?.EmbeddingProvider ?? "Simulated";

    public string ModelName => Credentials?.EmbeddingProvider.ToLowerInvariant() switch
    {
        "openai" => Credentials!.OpenAiEmbeddingModel,
        "google" => Credentials!.GoogleEmbeddingModel,
        _ => _simulatedClient.ModelName
    };

    public bool IsAvailable => Credentials?.EmbeddingProvider.ToLowerInvariant() switch
    {
        "openai" => !string.IsNullOrWhiteSpace(Credentials!.OpenAiApiKey),
        "google" => !string.IsNullOrWhiteSpace(Credentials!.GoogleApiKey),
        // Also covers Credentials being null in the first place (a null receiver's switch value is
        // null, which matches the discard pattern, not a string pattern) - "not configured" is exactly
        // as available as "Simulated", by design.
        _ => true
    };

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        if (credentials is null)
            return await _simulatedClient.GetEmbeddingAsync(text, cancellationToken);

        return credentials.EmbeddingProvider.ToLowerInvariant() switch
        {
            "openai" when !string.IsNullOrWhiteSpace(credentials.OpenAiApiKey)
                => await _openAiClient.GetEmbeddingAsync(credentials, text, cancellationToken),
            "google" when !string.IsNullOrWhiteSpace(credentials.GoogleApiKey)
                => await _googleClient.GetEmbeddingAsync(credentials, text, cancellationToken),
            _ => await _simulatedClient.GetEmbeddingAsync(text, cancellationToken)
        };
    }
}
