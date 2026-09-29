using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Realtime;

/// <summary>
/// Delivers a newly-raised notification to its group on <see cref="NotificationsHub"/>. Errors are
/// logged and swallowed, per <see cref="INotificationBroadcaster"/>'s contract - the row TenantNotifier/
/// PlatformNotifier already saved is the source of truth, and a client that misses the live push still
/// sees it the next time it polls or opens the bell.
/// </summary>
public class SignalRNotificationBroadcaster : INotificationBroadcaster
{
    private const string EventName = "NotificationReceived";

    private readonly IHubContext<NotificationsHub> _hubContext;
    private readonly ILogger<SignalRNotificationBroadcaster> _logger;

    public SignalRNotificationBroadcaster(IHubContext<NotificationsHub> hubContext, ILogger<SignalRNotificationBroadcaster> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyTenantAsync(Guid tenantId, object notification, CancellationToken cancellationToken = default)
    {
        try
        {
            await _hubContext.Clients.Group(NotificationsHub.TenantGroup(tenantId))
                .SendAsync(EventName, notification, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not push a live notification to tenant {TenantId}", tenantId);
        }
    }

    public async Task NotifyPlatformAsync(object notification, CancellationToken cancellationToken = default)
    {
        try
        {
            await _hubContext.Clients.Group(NotificationsHub.PlatformGroup)
                .SendAsync(EventName, notification, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not push a live notification to platform operators");
        }
    }
}
