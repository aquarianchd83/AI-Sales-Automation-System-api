using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio;

/// <summary>The one place that answers "may this user approve studio content?". Server-side callers (the approve and
/// publish endpoints in a later phase) use it so the rule cannot drift between the UI and the API.</summary>
public static class StudioApprovalRules
{
    public static bool CanApprove(StudioApprovalPolicy policy, IEnumerable<string> roles)
    {
        var held = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return policy switch
        {
            StudioApprovalPolicy.AdminAndSalesManager => held.Contains(AppRoles.Admin) || held.Contains(AppRoles.SalesManager),
            _ => held.Contains(AppRoles.Admin)
        };
    }
}
