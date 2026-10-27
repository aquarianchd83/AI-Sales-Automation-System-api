using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Infrastructure.Tenancy;

/// <summary>
/// One tenant's QR-code WhatsApp channel (a linked-device session on the self-hosted bridge) - 1:1 with Tenant, keyed by
/// <see cref="TenantId"/>, and entirely separate from <see cref="TenantWhatsAppConfig"/> (the Meta credentials), so switching provider
/// never touches either. Like that entity it is not on IApplicationDbContext: it is reached only through
/// <c>IWhatsAppChannelStore</c>, and implements <see cref="ITenantOwned"/> only to ride the reflective per-tenant query filter.
/// <see cref="WhatsAppChannelStore.FindByWebhookAsync"/> is the one deliberate <c>IgnoreQueryFilters()</c>: a webhook must find its channel
/// before any tenant is known.
/// </summary>
public class TenantWhatsAppChannel : ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The bridge instance this tenant owns, <c>t-{tenantId:N}</c>.</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>Ciphertext. The bridge's per-instance token (it also arrives inside every webhook body, where it is checked, then discarded).</summary>
    public string InstanceTokenCipher { get; set; } = string.Empty;

    /// <summary>SHA-256 of the unguessable path segment of this channel's webhook URL. The plaintext is shown to the bridge once, never stored.</summary>
    public string WebhookTokenHash { get; set; } = string.Empty;

    /// <summary>SHA-256 of the secret the bridge sends in a header with every webhook. Plaintext never stored.</summary>
    public string WebhookSecretHash { get; set; } = string.Empty;

    /// <summary>One of <c>BridgeStatuses</c>.</summary>
    public string Status { get; set; } = "NotConnected";

    /// <summary>Digits of the linked WhatsApp number, as the bridge reported it once the session opened.</summary>
    public string? ConnectedPhone { get; set; }

    public string? LastError { get; set; }

    public DateTime StatusUpdatedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
