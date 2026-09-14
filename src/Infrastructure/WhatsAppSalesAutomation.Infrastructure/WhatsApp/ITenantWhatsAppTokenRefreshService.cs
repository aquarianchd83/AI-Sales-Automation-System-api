namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>What one tenant's token refresh run concluded, short of an outright failure (which throws -
/// see <see cref="ITenantWhatsAppTokenRefreshService.RefreshAsync"/>).</summary>
public enum TenantWhatsAppTokenRefreshStatus
{
    /// <summary>The tenant has no WhatsApp config, or no access token in it - nothing to refresh. The
    /// normal state for a tenant that has not connected a WhatsApp Business Account.</summary>
    NotConfigured,

    /// <summary>There is a token, but no App ID or App Secret to exchange it with. Not a failure: a
    /// permanent System User token never needs refreshing. But a 60-day user token in this state will
    /// expire with nothing able to renew it, which is why the summary names what is missing.</summary>
    MissingAppCredentials,

    /// <summary>Checked before, and not inside the refresh window yet.</summary>
    NotDue,

    /// <summary>Checked before, and Meta reported no expiry for it - left alone rather than exchanged
    /// every day.</summary>
    NeverExpires,

    Refreshed
}

/// <param name="Summary">One line for the tenant's job row on the Background Jobs screen. Never contains
/// the token, the App Secret, or the request URL (which carries both).</param>
public record TenantWhatsAppTokenRefreshResult(
    TenantWhatsAppTokenRefreshStatus Status,
    DateTime? ExpiresAtUtc,
    string Summary);

/// <summary>
/// Keeps one tenant's WhatsApp Cloud API access token from expiring, by exchanging it with Meta for a
/// fresh long-lived one using that tenant's own Meta App credentials - the token every one of that
/// tenant's sends actually authenticates with (see MetaWhatsAppCloudApiClient, which only ever uses the
/// per-call tenant credentials).
///
/// Replaces the pre-multi-tenant platform-level refresh, which exchanged a single global token held in
/// WhatsAppAccessTokenState. Nothing had read that token since BYO-WABA credentials moved onto
/// TenantWhatsAppConfig, so that job ran daily keeping a token alive that no send ever used, while no
/// tenant's real token was refreshed by anything.
/// </summary>
public interface ITenantWhatsAppTokenRefreshService
{
    /// <summary>
    /// Works on <paramref name="tenantId"/> explicitly rather than the ambient tenant, so it is correct
    /// whichever scope resolves it.
    ///
    /// Returns for every state that is not a fault, including "nothing to do". Throws
    /// <see cref="InvalidOperationException"/> when a refresh was due and could not happen - Meta
    /// rejected the exchange, the request failed, or the stored secrets could not be decrypted - so the
    /// per-tenant job runner records a Failed outcome and counts it toward the tenant's consecutive
    /// failures. The stored token is never modified on any failure path.
    /// </summary>
    Task<TenantWhatsAppTokenRefreshResult> RefreshAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
