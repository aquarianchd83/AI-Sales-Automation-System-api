namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Reads one tenant's AI provider choice and API keys, and writes them on a PlatformSuperAdmin's
/// behalf - see <c>ITenantWhatsAppConfigProvider</c>'s own doc comment for the identical encrypt/
/// decrypt-boundary reasoning and for why write access moved off the tenant's own self-service
/// settings screen and onto the Platform Admin Console. <c>AiServiceFactory</c>/
/// <c>TenantEmbeddingService</c>/<c>TenantEmbeddingProviderCatalog</c> (Infrastructure) are what
/// actually consume the credential-reading half to pick and configure the right concrete client per
/// tenant, per call.
/// </summary>
public interface ITenantAiConfigProvider
{
    /// <summary>The calling tenant's AI config (from <see cref="ITenantContext"/>), or null if this
    /// tenant has never configured one - defaults to "Simulated" for both Provider and
    /// EmbeddingProvider in that case, same as a brand-new trial tenant's WhatsApp config being
    /// absent rather than a row of empty strings. Cached for the lifetime of the current scope - see
    /// ITenantWhatsAppConfigProvider.GetForCurrentTenantAsync's own doc comment on why.</summary>
    Task<TenantAiCredentials?> GetForCurrentTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>Masked view of the calling tenant's config for their own (read-only) settings screen -
    /// API keys are never exposed in full, only whether each is set. Null if not yet configured
    /// (defaults apply).</summary>
    Task<TenantAiProviderConfigDto?> GetConfigForCurrentTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>Deprecated call path kept only so <see cref="GetConfigForCurrentTenantAsync"/> can
    /// resolve the ambient tenant and delegate - throws <see cref="InvalidOperationException"/> if
    /// actually invoked without a tenant in scope. No controller writes through this anymore; see
    /// <see cref="SaveConfigForTenantAsync"/>.</summary>
    Task<TenantAiProviderConfigDto> SaveConfigForCurrentTenantAsync(
        UpdateTenantAiProviderConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Masked view of the given tenant's config, for the Platform Admin Console's Tenant
    /// detail screen. Null if not yet configured.</summary>
    Task<TenantAiProviderConfigDto?> GetConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates <paramref name="tenantId"/>'s config on a PlatformSuperAdmin's
    /// behalf - the only way a tenant's AI provider config gets written now. Same "null leaves it
    /// unchanged, empty string clears it" convention as every other field here.</summary>
    Task<TenantAiProviderConfigDto> SaveConfigForTenantAsync(
        Guid tenantId, UpdateTenantAiProviderConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Removes the tenant's AI provider config entirely - back to the built-in "Simulated"
    /// defaults, the same state a brand-new trial tenant starts in. A no-op (not an error) if the
    /// tenant had no config to begin with.</summary>
    Task DeleteConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Already-decrypted AI provider config, ready to use against whichever provider's client -
/// never logged or returned from any endpoint as-is. Field-for-field mirror of the pre-multi-tenant
/// global AiProviderSettings shape.</summary>
public record TenantAiCredentials(
    string Provider,
    string EmbeddingProvider,
    string? AnthropicApiKey,
    string AnthropicModel,
    string AnthropicApiVersion,
    string AnthropicBaseUrl,
    string? OpenAiApiKey,
    string OpenAiChatModel,
    string OpenAiEmbeddingModel,
    string OpenAiBaseUrl,
    string? GoogleApiKey,
    string GoogleChatModel,
    string GoogleEmbeddingModel,
    string GoogleBaseUrl);

public record TenantAiProviderConfigDto(
    string Provider,
    string EmbeddingProvider,
    bool HasAnthropicApiKey,
    string AnthropicModel,
    bool HasOpenAiApiKey,
    string OpenAiChatModel,
    string OpenAiEmbeddingModel,
    bool HasGoogleApiKey,
    string GoogleChatModel,
    string GoogleEmbeddingModel);

/// <summary>Body of PUT the tenant AI settings endpoint. Any null field leaves the currently-stored
/// value unchanged (or the built-in default, on first save) - same convention as
/// UpdateTenantWhatsAppConfigRequest.</summary>
public record UpdateTenantAiProviderConfigRequest(
    string? Provider,
    string? EmbeddingProvider,
    string? AnthropicApiKey,
    string? AnthropicModel,
    string? OpenAiApiKey,
    string? OpenAiChatModel,
    string? OpenAiEmbeddingModel,
    string? GoogleApiKey,
    string? GoogleChatModel,
    string? GoogleEmbeddingModel);
