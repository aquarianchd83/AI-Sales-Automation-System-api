namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Pushes a live update to connected Agent Inbox clients. Implemented in Infrastructure via SignalR.
/// Payloads are deliberately thin ("something changed, here is the id") rather than full DTOs - a
/// client refetches the conversation/handoff itself, so this cannot drift out of sync with what
/// GetByIdAsync would actually return.
/// </summary>
public interface INotificationService
{
    Task NotifyNewInboundMessageAsync(Guid conversationId, Guid customerId, string? textPreview, CancellationToken cancellationToken = default);

    Task NotifyNewHandoffAsync(Guid handoffId, Guid conversationId, string triggerReason, CancellationToken cancellationToken = default);

    /// <summary>An outbound message's delivery status advanced (Sent/Delivered/Read/Failed) - pushed so
    /// an open conversation detail page reflects a "read" receipt etc. live, instead of only on the
    /// next manual refresh. <paramref name="status"/> is the new MessageStatus name (e.g. "Read").</summary>
    Task NotifyMessageStatusUpdatedAsync(Guid conversationId, Guid messageId, string status, CancellationToken cancellationToken = default);
}
