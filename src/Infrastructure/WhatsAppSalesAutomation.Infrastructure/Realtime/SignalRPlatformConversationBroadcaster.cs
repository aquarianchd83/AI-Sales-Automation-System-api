using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Realtime;

/// <summary>Tells every open Platform Admin Console (the platform-admins group on <see cref="NotificationsHub"/>) that a Tenant conversations
/// chat changed - a reply came in or a status moved - so the page refetches it. Carries only the chat's id; errors are logged and swallowed,
/// the page also refreshes on its own timer.</summary>
public class SignalRPlatformConversationBroadcaster : IPlatformConversationBroadcaster
{
    public const string EventName = "PlatformConversationUpdated";

    private readonly IHubContext<NotificationsHub> _hubContext;
    private readonly ILogger<SignalRPlatformConversationBroadcaster> _logger;

    public SignalRPlatformConversationBroadcaster(IHubContext<NotificationsHub> hubContext, ILogger<SignalRPlatformConversationBroadcaster> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task ConversationUpdatedAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _hubContext.Clients.Group(NotificationsHub.PlatformGroup).SendAsync(EventName, new { conversationId }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not push a platform conversation update");
        }
    }
}
