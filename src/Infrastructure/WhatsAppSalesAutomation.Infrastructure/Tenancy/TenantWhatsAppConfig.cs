using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Infrastructure.Tenancy;

/// <summary>
/// One tenant's own WhatsApp Business Account (BYO-WABA) credentials - 1:1 with Tenant, keyed
/// directly by <see cref="TenantId"/> rather than a separate BaseEntity-style Guid Id, the same "no
/// row = not configured yet" treatment as a missing config, not a placeholder row with empty fields.
/// Deliberately NOT on <see cref="Application.Common.Interfaces.IApplicationDbContext"/> - same
/// Infrastructure-internal reasoning as <c>WhatsAppAccessTokenState</c>/<c>AppSetting</c>: the
/// Application layer never needs this entity itself, only what
/// <see cref="Application.Common.Interfaces.ITenantWhatsAppConfigProvider"/> exposes (already-decrypted
/// credentials). Implements <see cref="ITenantOwned"/> purely to piggyback on
/// <c>ApplicationDbContext</c>'s reflective per-tenant query filter and
/// <c>TenantStampingSaveChangesInterceptor</c> for the ordinary "read/write my own tenant's row" path -
/// <see cref="TenantWhatsAppConfigProvider.GetByPhoneNumberIdAsync"/> is the one deliberate exception
/// that bypasses the filter with <c>IgnoreQueryFilters()</c>, since webhook routing has to find a row
/// *before* any tenant is known.
/// </summary>
public class TenantWhatsAppConfig : ITenantOwned
{
    /// <summary>Doubles as the primary key - true 1:1 with Tenant, not just a unique index.</summary>
    public Guid TenantId { get; set; }

    public string PhoneNumberId { get; set; } = string.Empty;

    public string WhatsAppBusinessAccountId { get; set; } = string.Empty;

    /// <summary>Ciphertext (AppSettingsSecretProtection) - never read or written unencrypted. A long-
    /// lived System User token is expected here; BYO-WABA means auto-refresh is each tenant's own
    /// Meta App's concern, not something this platform's WhatsAppTokenRefreshService (built for the
    /// single pre-multi-tenant platform account) attempts on their behalf.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Ciphertext. Verifies X-Hub-Signature-256 on every inbound webhook claiming to be this
    /// tenant's <see cref="PhoneNumberId"/> - see WebhooksController.Receive.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Ciphertext. Stored for schema completeness/a future per-tenant handshake, but NOT what
    /// WebhooksController.Verify actually checks today - the GET verification handshake keeps using
    /// one platform-global WhatsAppSettings.WebhookVerifyToken, since Meta subscribes per-App (one
    /// platform Meta App under BYO-WABA), not per-WABA. See that method's own doc comment.</summary>
    public string? WebhookVerifyToken { get; set; }

    public string ApiVersion { get; set; } = "v19.0";

    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com/";

    /// <summary>True once PhoneNumberId/AccessToken/AppSecret are all non-empty - a simple "is this
    /// tenant's WABA usable" flag for the frontend, not a live Meta connectivity check.</summary>
    public bool IsConnected { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
