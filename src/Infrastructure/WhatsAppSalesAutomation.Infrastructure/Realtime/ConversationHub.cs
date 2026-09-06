using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace WhatsAppSalesAutomation.Infrastructure.Realtime;

/// <summary>
/// Agent Inbox real-time channel, mapped at /hubs/conversations. No client-callable hub methods
/// exist yet - Phase 4 only pushes server-to-client notifications (see SignalRNotificationService);
/// a client just connects and listens for "NewInboundMessage"/"NewHandoff"/"MessageStatusUpdated".
/// </summary>
[Authorize]
public class ConversationHub : Hub
{
}
