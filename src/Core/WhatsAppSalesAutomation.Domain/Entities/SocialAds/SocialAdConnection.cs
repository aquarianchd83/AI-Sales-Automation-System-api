using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.SocialAds;

/// <summary>
/// A tenant's link to its Meta (Facebook + Instagram) ad account, made by one Facebook login. The access
/// token is stored protected, never returned by any API, and only ever used to read ad spend and results.
/// One connection per tenant.
/// </summary>
public class SocialAdConnection : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public SocialAdConnectionStatus Status { get; set; } = SocialAdConnectionStatus.PendingAccountSelection;

    /// <summary>Meta's id for the ad account, without the "act_" prefix. Null until one is chosen.</summary>
    public string? AdAccountId { get; set; }

    public string? AdAccountName { get; set; }

    /// <summary>The ad account's billing currency (ISO 4217) - the currency every synced spend row is in.</summary>
    public string? CurrencyCode { get; set; }

    /// <summary>Data-protection ciphertext of the long-lived user access token.</summary>
    public string AccessToken { get; set; } = string.Empty;

    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>The ad accounts the login can see, as JSON, kept only while the tenant is choosing one.</summary>
    public string? CandidateAccountsJson { get; set; }

    public DateTime ConnectedAt { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    /// <summary>A short, token-free description of why the last sync failed; null after a good one.</summary>
    public string? LastSyncError { get; set; }
}
