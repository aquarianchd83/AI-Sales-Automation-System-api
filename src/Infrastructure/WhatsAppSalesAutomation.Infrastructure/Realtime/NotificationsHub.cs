using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Infrastructure.Identity;

namespace WhatsAppSalesAutomation.Infrastructure.Realtime;

/// <summary>
/// In-app notification bell channel, mapped at /hubs/notifications. A connecting client is placed into
/// exactly one group based on its own JWT - the tenant it belongs to, or the shared platform-operator
/// group - so a push reaches every open tab for that tenant (or every PlatformSuperAdmin) without the
/// server needing to track individual connections itself. No client-callable methods: this is
/// server-to-client only, same shape as <see cref="ConversationHub"/>.
/// </summary>
[Authorize]
public class NotificationsHub : Hub
{
    public const string PlatformGroup = "platform-admins";

    public static string TenantGroup(Guid tenantId) => $"tenant:{tenantId}";

    public override async Task OnConnectedAsync()
    {
        var tenantIdValue = Context.User?.FindFirstValue(JwtClaimNames.TenantId);
        if (Guid.TryParse(tenantIdValue, out var tenantId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId));
        }
        else if (Context.User?.IsInRole(AppRoles.PlatformSuperAdmin) == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, PlatformGroup);
        }

        await base.OnConnectedAsync();
    }
}
