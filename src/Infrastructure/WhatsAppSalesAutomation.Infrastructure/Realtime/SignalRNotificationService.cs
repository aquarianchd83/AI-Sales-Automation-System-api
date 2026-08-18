using Microsoft.AspNetCore.SignalR;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Realtime;

/// <summary>
/// Broadcasts to every connected client rather than a per-agent or per-conversation group - there is
/// no concept yet of which agents are watching which conversation, so "everyone in the inbox sees
/// every update" is the honest MVP behaviour, not a shortcut hiding a real filtering requirement.
/// </summary>
public class SignalRNotificationService : INotificationService
{
    private readonly IHubContext<ConversationHub> _hubContext;

    public SignalRNotificationService(IHubContext<ConversationHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task NotifyNewInboundMessageAsync(Guid conversationId, Guid customerId, string? textPreview, CancellationToken cancellationToken = default) =>
        _hubContext.Clients.All.SendAsync("NewInboundMessage", new { conversationId, customerId, textPreview }, cancellationToken);

    public Task NotifyNewHandoffAsync(Guid handoffId, Guid conversationId, string triggerReason, CancellationToken cancellationToken = default) =>
        _hubContext.Clients.All.SendAsync("NewHandoff", new { handoffId, conversationId, triggerReason }, cancellationToken);
}
