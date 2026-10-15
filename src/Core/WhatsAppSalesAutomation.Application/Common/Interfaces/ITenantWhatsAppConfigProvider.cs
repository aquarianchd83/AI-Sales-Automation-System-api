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

    /// <summary>
    /// The other cross-tenant lookup, for account-level webhooks (quality updates) that name the WhatsApp Business
    /// Account and the number's display form but carry no <c>phone_number_id</c>. One tenant on that WABA is returned
    /// as is; if several share it, the one whose own WhatsApp number matches <paramref name="displayPhoneNumber"/>
    /// (compared as digits). Null when nobody matches - never a guess between tenants.
    /// </summary>
    Task<TenantWhatsAppLookupResult?> GetByWabaIdAsync(string wabaId, string? displayPhoneNumber, CancellationToken cancellationToken = default);

    /// <summary>Masked view of the calling tenant's config for the settings screen - never exposes
    /// AccessToken/AppSecret/WebhookVerifyToken in full, only whether each is set (same convention as
    /// SettingItemDto.HasValue/ValueHint). Null if not yet configured.</summary>
    Task<TenantWhatsAppConfigDto?> GetConfigForCurrentTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>Records which connection mode the tenant picked, on its own row, and touches nothing else (credentials, verification
    /// and connected state are left exactly as they are).</summary>
    /// <exception cref="FluentValidation.ValidationException">The mode is not one of <see cref="WhatsAppConnectionModes"/>.</exception>
    /// <exception cref="Exceptions.ConflictException">The tenant has no WhatsApp connection saved yet, so there is no row to hold the
    /// choice. It is saved together with the connection when one is made.</exception>
    Task<TenantWhatsAppConfigDto> SetConnectionModeForTenantAsync(Guid tenantId, string mode, Guid? updatedByUserId, CancellationToken cancellationToken = default);

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
    /// what the Platform Admin Console's WhatsApp Connections screen (spec item #5) lists. Omits token
    /// expiry for now - see <see cref="TenantWhatsAppConnectionSummary"/>'s own doc comment.</summary>
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
    string ApiBaseUrl,
    string? AppId = null);

/// <summary>Result of the cross-tenant, by-phone-number-id webhook routing lookup.</summary>
public record TenantWhatsAppLookupResult(Guid TenantId, TenantWhatsAppCredentials Credentials);

/// <param name="AppId">Not a secret, returned in full (not a Has* flag). Stored for record-keeping/a
/// future per-tenant-App architecture - see <c>TenantWhatsAppConfig.AppId</c>'s own doc comment for
/// why it isn't yet what webhook routing actually keys off.</param>
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
    bool HasWebhookVerifyToken,
    string? AppId,
    DateTime? VerifiedAtUtc = null,
    string? VerificationError = null,
    string? VerifiedDisplayPhoneNumber = null,
    string? VerifiedName = null,
    string? ConnectionMode = null)
{
    /// <summary>Configured and confirmed live by Meta since the last save - what onboarding's WhatsApp step needs.</summary>
    public bool IsVerified => IsConnected && VerifiedAtUtc is not null;
}

/// <summary>
/// "Verify connection": asks Meta, with the tenant's stored token, for the configured phone number. Records the
/// outcome on the tenant's config (verified time and number, or Meta's error) and returns the updated config.
/// Used by both the tenant's own Admin and the Platform Admin Console.
/// </summary>
public interface ITenantWhatsAppConnectionVerifier
{
    /// <exception cref="Exceptions.ConflictException">Nothing to verify yet: no phone number or token saved.</exception>
    Task<TenantWhatsAppConfigDto> VerifyAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Body of PUT the tenant WhatsApp settings endpoint. <see cref="AccessToken"/>/<see cref="AppSecret"/>/
/// <see cref="WebhookVerifyToken"/> null leaves the currently-stored value unchanged - see
/// ITenantWhatsAppConfigProvider.SaveConfigForCurrentTenantAsync's own doc comment. <see cref="AppId"/>
/// is not a secret and follows the same convention as ApiVersion/ApiBaseUrl: blank/whitespace leaves
/// the stored value unchanged (there is no way to clear it back to null through this endpoint, same
/// limitation those two already have).</summary>
public record UpdateTenantWhatsAppConfigRequest(
    string PhoneNumberId,
    string WhatsAppBusinessAccountId,
    string? AccessToken,
    string? AppSecret,
    string? ApiVersion,
    string? ApiBaseUrl,
    string? WebhookVerifyToken = null,
    string? AppId = null,
    string? ConnectionMode = null);

/// <summary>How a tenant connects WhatsApp: "Automatic" (Connect with Meta) or "Manual" (details typed in). The value is stored on the
/// tenant's own <c>TenantWhatsAppConfig.ConnectionMode</c>; null there means the tenant has not chosen yet. Both modes write the same
/// credentials, so switching mode never touches them.</summary>
public static class WhatsAppConnectionModes
{
    public const string Automatic = "Automatic";
    public const string Manual = "Manual";

    /// <summary>Exact, case-sensitive match - the API only ever stores one of the two constants.</summary>
    public static bool IsSupported(string? mode) => mode is Automatic or Manual;
}

/// <summary>Body of PUT tenant-settings/whatsapp/connection-mode.</summary>
public record SetWhatsAppConnectionModeRequest(string Mode);

/// <summary>
/// One tenant's row on the Platform Admin Console's WhatsApp Connections screen. Has no token-expiry
/// field yet, although one could now be filled in honestly: each tenant's own refresh job records Meta's
/// reported expiry on <c>TenantWhatsAppConfig.AccessTokenExpiresAtUtc</c>. Surfacing it here is a
/// separate change. Until then <see cref="IsConnected"/> is what this reports - not "is the token still
/// valid right now against Meta," just "did the tenant finish saving a complete config." The Background
/// Jobs screen's whatsapp-token-refresh row is where a failing or un-refreshable token shows up.
/// </summary>
public record TenantWhatsAppConnectionSummary(
    Guid TenantId,
    string? PhoneNumberId,
    string? WhatsAppBusinessAccountId,
    bool IsConnected,
    DateTime UpdatedAtUtc);
