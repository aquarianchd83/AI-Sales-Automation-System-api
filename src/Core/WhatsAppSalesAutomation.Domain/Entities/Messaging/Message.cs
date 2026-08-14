using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Messaging;

/// <summary>
/// One outbound (Phase 3) or inbound (Phase 4) WhatsApp message. Deliberately has no
/// <c>ConversationId</c> yet - <c>Conversations</c> does not exist until Phase 4, and this table is
/// designed to be the transcript both phases share, so that FK is added then rather than guessed at now.
/// </summary>
public class Message : BaseEntity
{
    public Guid CustomerId { get; set; }

    /// <summary>Set for campaign-originated sends; null for anything sent outside a campaign (Phase 4/5).</summary>
    public Guid? CampaignCustomerId { get; set; }

    /// <summary>Which step this message is - 0 for Initial, 1-4 for a follow-up. Lets the retry job
    /// re-resolve the step's template/text without having to infer it from campaign customer state.</summary>
    public int? CampaignStepNumber { get; set; }

    public MessageDirection Direction { get; set; } = MessageDirection.Outbound;

    public MessageType MessageType { get; set; } = MessageType.Template;

    /// <summary>The resolved text actually sent (placeholders already substituted).</summary>
    public string? Text { get; set; }

    /// <summary>Snapshot of which template was used, since the template itself can later be edited or retired.</summary>
    public string? TemplateName { get; set; }

    /// <summary>Populated once WhatsApp accepts the send. Unique when present - the inbound-side dedup key (Phase 4).</summary>
    public string? WhatsAppMessageId { get; set; }

    /// <summary>Synthetic outbound dedup key: <c>{CampaignCustomerId}:step{StepNumber}</c>. Always unique.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public MessageStatus Status { get; set; } = MessageStatus.Queued;

    public string? FailureReason { get; set; }

    /// <summary>How many send attempts have been made. <c>MessageStatusRetryJob</c> stops retrying past a configured max.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Backoff gate for the retry job - null means either not yet attempted or no retry needed.</summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public DateTime? ReadAt { get; set; }
}
