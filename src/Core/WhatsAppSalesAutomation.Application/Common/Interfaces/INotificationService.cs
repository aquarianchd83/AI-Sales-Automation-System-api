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
}

/// <summary>
/// Pushes a freshly-raised in-app notification to whichever bell it belongs on, live, so the tenant or
/// platform-operator UI can prepend it without the viewer refreshing the page. Implemented in
/// Infrastructure via SignalR (<c>NotificationsHub</c>), one group per tenant and one shared group for
/// every platform operator. The payload is the same DTO shape the REST list endpoint already returns,
/// so a client can push it straight into its existing list with no separate mapping to keep in sync.
/// Never throws - a client that missed the push still sees the notification on its next poll or bell open.
/// </summary>
public interface INotificationBroadcaster
{
    Task NotifyTenantAsync(Guid tenantId, object notification, CancellationToken cancellationToken = default);

    Task NotifyPlatformAsync(object notification, CancellationToken cancellationToken = default);

    /// <summary>Tells a tenant's open screens that one of its background jobs just finished (whatever the
    /// outcome), so a page showing what that job changes - the campaign grid, say - can refetch itself.
    /// A separate event from the bell's notifications: it carries only the job type, nothing to display.</summary>
    Task NotifyTenantJobFinishedAsync(Guid tenantId, string jobType, CancellationToken cancellationToken = default);

    /// <summary>The counterpart sent when a job's run begins, so a screen can show it as in progress (and grey out
    /// its "run now" button) until the matching finished event arrives.</summary>
    Task NotifyTenantJobStartedAsync(Guid tenantId, string jobType, CancellationToken cancellationToken = default);
}
