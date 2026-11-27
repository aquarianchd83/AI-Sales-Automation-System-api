using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// A WhatsApp chat between the PLATFORM's own number (PlatformWhatsApp settings) and one phone number - normally a tenant's contact.
/// Platform-owned, deliberately NOT <see cref="ITenantOwned"/>: the tenant never sees it, the Platform Admin Console does.
/// <see cref="TenantId"/> only says which tenant the number belongs to (null when an unknown number wrote to the platform number).
/// One row per phone number, so a reply from WhatsApp always lands in the same chat.
/// </summary>
public class PlatformConversation : BaseEntity
{
    public Guid? TenantId { get; set; }

    /// <summary>The other side's number, E.164 with the leading +.</summary>
    public string PhoneE164 { get; set; } = string.Empty;

    /// <summary>The WhatsApp profile name the person last wrote with, when Meta sent one.</summary>
    public string? ContactName { get; set; }

    public DateTime? LastMessageAtUtc { get; set; }

    /// <summary>When the other side last wrote. Free text is only delivered within 24 hours of this; after that only an approved template goes.</summary>
    public DateTime? LastInboundAtUtc { get; set; }

    public string? LastMessagePreview { get; set; }

    /// <summary>Inbound messages the platform admin has not opened yet.</summary>
    public int UnreadCount { get; set; }

    public ICollection<PlatformConversationMessage> Messages { get; set; } = new List<PlatformConversationMessage>();
}
