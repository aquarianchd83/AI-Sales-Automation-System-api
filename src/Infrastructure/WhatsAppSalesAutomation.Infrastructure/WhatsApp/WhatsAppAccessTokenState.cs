namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Retired: nothing reads or writes this any more. Each tenant's own token lives on TenantWhatsAppConfig
/// and is refreshed by that tenant's own job (TenantWhatsAppTokenRefreshService), and no send had read
/// this row since BYO-WABA. The entity and table are kept only so an existing row - which holds a real
/// access token - is not deleted by a migration nobody asked for; dropping both is a separate decision.
///
/// Originally: single-row table holding the WhatsApp Cloud API's current access token, refreshed in place
/// by the pre-multi-tenant global WhatsAppTokenRefreshService. Deliberately NOT a Domain entity reached through
/// IApplicationDbContext - the Application layer has no business logic reason to know a WhatsApp
/// access token exists; this is purely how MetaWhatsAppCloudApiClient authenticates, an
/// Infrastructure-internal concern. It lives in the same database as everything else only for
/// operational simplicity (one connection string, one migration history).
///
/// <see cref="Id"/> is always 1 - a fixed singleton row rather than a Guid-keyed BaseEntity-style
/// table, since there is exactly one WhatsApp Business integration per deployment of this app and
/// "which row is current" would otherwise need its own tie-breaking rule for no benefit.
/// </summary>
public class WhatsAppAccessTokenState
{
    public int Id { get; set; }

    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Null if Meta's token-exchange response omitted expires_in (observed for some
    /// System User token configurations that are set to never expire) - a real "we don't know, don't
    /// guess" rather than a fabricated far-future date.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime RefreshedAt { get; set; }
}
