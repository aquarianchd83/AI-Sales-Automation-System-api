using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>One message of a <see cref="PlatformConversation"/>: sent by a platform admin from the platform number, or received on it.</summary>
public class PlatformConversationMessage : BaseEntity
{
    public Guid ConversationId { get; set; }

    public PlatformConversation? Conversation { get; set; }

    public MessageDirection Direction { get; set; }

    /// <summary>What was said. For a template send, the template's text with its values filled in.</summary>
    public string? Body { get; set; }

    /// <summary>Meta's message type for an inbound message ("text", "image", ...).</summary>
    public string MessageType { get; set; } = "text";

    /// <summary>The Meta template name, when this went out as a template.</summary>
    public string? TemplateName { get; set; }

    public MessageStatus Status { get; set; } = MessageStatus.Queued;

    /// <summary>Meta's wamid - how status webhooks and redelivered inbound messages are matched.</summary>
    public string? WhatsAppMessageId { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>The platform admin who sent it (outbound only).</summary>
    public Guid? SentByUserId { get; set; }

    public string? SentByEmail { get; set; }

    /// <summary>When it was sent or received, by Meta's clock for an inbound message.</summary>
    public DateTime SentAtUtc { get; set; }
}
