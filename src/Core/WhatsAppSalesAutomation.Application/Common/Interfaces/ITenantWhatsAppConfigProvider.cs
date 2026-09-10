namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Reads one tenant's WhatsApp Business Account credentials, and writes them on a PlatformSuperAdmin's
/// behalf (see <see cref="SaveConfigForTenantAsync"/>/<see cref="DeleteConfigForTenantAsync"/>) -
/// implemented in Infrastructure against the Infrastructure-internal <c>TenantWhatsAppConfig</c> table,
/// decrypting on the way out and encrypting on the way in so nothing above this interface ever sees
/// ciphertext or plaintext secrets mixed up. <c>WhatsAppServiceFactory</c> (Infrastructure) is the main
/// consumer of the credential-reading half, resolving them fresh on every outbound call the same way
/// the old IWhatsAppTokenStore did for the single pre-multi-tenant token.
///
/// Write access used to be the tenant's own self-service settings screen (Phase B); it now belongs to
/// the Platform Admin Console exclusively - see <c>PlatformTenantConfigController</c>'s own doc
/// comment for why a tenant no longer edits or deletes its own WhatsApp credentials. A tenant admin
/// can still read the masked status of their own config (<see cref="GetConfigForCurrentTenantAsync"/>),
/// unchanged.
/// </summary>
public interface ITenantWhatsAppConfigProvider
{
    /// <summary>The calling tenant's credentials (from <see cref="ITenantContext"/>), or null if this
    /// tenant has never configured a WhatsApp Business Account - "no row" is a perfectly normal state
    /// (a brand-new trial tenant), not an error. Result is cached for the lifetime of the current
    /// scope, so repeated per-call resolution (WhatsAppServiceFactory, one lookup per outbound send)
    /// costs one DB round trip per request/job, not one per send.</summary>
    Task<TenantWhatsAppCredentials?> GetForCurrentTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The one deliberately cross-tenant lookup: resolves which tenant owns a given Meta
    /// <c>phone_number_id</c>, for WebhooksController.Receive to find out who an inbound webhook
    /// belongs to *before* any tenant is in scope - the only legitimate place this system ever needs
    /// to search WhatsApp credentials across every tenant at once. Null if no tenant has connected
    /// that phone number.
    /// </summary>
    Task<TenantWhatsAppLookupResult?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken = default);

    /// <summary>Masked view of the calling tenant's config for the settings screen - never exposes
    /// AccessToken/AppSecret/WebhookVerifyToken in full, only whether each is set (same convention as
    /// SettingItemDto.HasValue/ValueHint). Null if not yet configured.</summary>
    Task<TenantWhatsAppConfigDto?> GetConfigForCurrentTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>Deprecated call path kept only for <see cref="GetConfigForCurrentTenantAsync"/>'s own
    /// read-only use - throws <see cref="InvalidOperationException"/> if actually invoked without a
    /// tenant in scope, same as before. No controller writes through this anymore; see
    /// <see cref="SaveConfigForTenantAsync"/>.</summary>
    Task<TenantWhatsAppConfigDto> SaveConfigForCurrentTenantAsync(
        UpdateTenantWhatsAppConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Masked view of the given tenant's config, for the Platform Admin Console's Tenant
    /// detail screen. Null if not yet configured.</summary>
    Task<TenantWhatsAppConfigDto?> GetConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates <paramref name="tenantId"/>'s config on a PlatformSuperAdmin's
    /// behalf - the only way a tenant's WhatsApp credentials get written now. Same "null leaves it
    /// unchanged, empty string clears it" convention as every other field here.</summary>
    Task<TenantWhatsAppConfigDto> SaveConfigForTenantAsync(
        Guid tenantId, UpdateTenantWhatsAppConfigRequest request, Guid? updatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Removes the tenant's WhatsApp config entirely - back to "not connected," the same
    /// state a brand-new trial tenant starts in. A no-op (not an error) if the tenant had no config
    /// to begin with.</summary>
    Task DeleteConfigForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Cross-tenant, masked connection status for every tenant that has ever saved a config -
    /// what the Platform Admin Console's WhatsApp Connections screen (spec item #5) lists. Deliberately
    /// omits any expiry - see <see cref="TenantWhatsAppConnectionSummary"/>'s own doc comment for why
    /// that column can't be filled in honestly today.</summary>
    Task<IReadOnlyList<TenantWhatsAppConnectionSummary>> GetAllConnectionSummariesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Already-decrypted credentials, ready to use against Meta's Cloud API - never logged or
/// returned from any endpoint as-is.</summary>
public record TenantWhatsAppCredentials(
    string PhoneNumberId,
    string WhatsAppBusinessAccountId,
    string AccessToken,
    string AppSecret,
    string ApiVersion,
    string ApiBaseUrl);

/// <summary>Result of the cross-tenant, by-phone-number-id webhook routing lookup.</summary>
public record TenantWhatsAppLookupResult(Guid TenantId, TenantWhatsAppCredentials Credentials);

/// <param name="HasWebhookVerifyToken">Stored for schema completeness/a future per-tenant handshake -
/// see <c>TenantWhatsAppConfig.WebhookVerifyToken</c>'s own doc comment. Not what
/// WebhooksController.Verify actually checks today: Meta subscribes per-App (one platform Meta App
/// under BYO-WABA), not per-WABA, so the GET verification handshake still uses the one platform-
/// global WhatsAppSettings.WebhookVerifyToken regardless of what's saved here.</param>
public record TenantWhatsAppConfigDto(
    string? PhoneNumberId,
    string? WhatsAppBusinessAccountId,
    bool HasAccessToken,
    bool HasAppSecret,
    string ApiVersion,
    string ApiBaseUrl,
    bool IsConnected,
    bool HasWebhookVerifyToken);

/// <summary>Body of PUT the tenant WhatsApp settings endpoint. <see cref="AccessToken"/>/<see cref="AppSecret"/>/
/// <see cref="WebhookVerifyToken"/> null leaves the currently-stored value unchanged - see
/// ITenantWhatsAppConfigProvider.SaveConfigForCurrentTenantAsync's own doc comment.</summary>
public record UpdateTenantWhatsAppConfigRequest(
    string PhoneNumberId,
    string WhatsAppBusinessAccountId,
    string? AccessToken,
    string? AppSecret,
    string? ApiVersion,
    string? ApiBaseUrl,
    string? WebhookVerifyToken = null);

/// <summary>
/// One tenant's row on the Platform Admin Console's WhatsApp Connections screen. Deliberately has no
/// token-expiry field: BYO-WABA tenant credentials are a long-lived System User token that this
/// platform's own <c>WhatsAppTokenRefreshService</c>/<c>WhatsAppAccessTokenState</c> does not track -
/// that mechanism only ever concerned the single pre-multi-tenancy platform-global fallback account,
/// never a tenant's own Meta App. <see cref="IsConnected"/> is therefore the honest ceiling on what
/// this system can currently report about a tenant's WABA health - not "is the token still valid
/// right now against Meta," just "did the tenant finish saving a complete config."
/// </summary>
public record TenantWhatsAppConnectionSummary(
    Guid TenantId,
    string? PhoneNumberId,
    string? WhatsAppBusinessAccountId,
    bool IsConnected,
    DateTime UpdatedAtUtc);
