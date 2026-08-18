using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace WhatsAppSalesAutomation.Infrastructure.Realtime;

/// <summary>
/// Agent Inbox real-time channel, mapped at /hubs/conversations. No client-callable hub methods
/// exist yet - Phase 4 only pushes server-to-client notifications (see SignalRNotificationService);
/// a client just connects and listens for "NewInboundMessage"/"NewHandoff". No Angular client exists
/// in this repo to consume it (backend-only, per the Phase 2 scope note), so this has been verified
/// with a minimal test client, not a real one.
/// </summary>
[Authorize]
public class ConversationHub : Hub
{
}
