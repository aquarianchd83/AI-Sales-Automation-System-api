using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Whether a message template may be used for sending, for the current tenant. Through Meta a template must be active AND Approved by Meta's
/// review. A tenant on the WhatsApp QR bridge has no Meta review at all (the bridge sends the filled-in template text as plain text and never
/// registers templates), so for that tenant an active template is enough - otherwise its templates would sit at Pending forever.
/// </summary>
public interface ITemplateSendPolicy
{
    bool IsSendable(MessageTemplate template);
}

/// <summary>The Meta rule, used whenever no provider-aware policy is registered.</summary>
public static class TemplateSendRule
{
    public static bool MetaSendable(MessageTemplate template) =>
        template.IsActive && template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved;

    public static bool CanSend(this ITemplateSendPolicy? policy, MessageTemplate template) =>
        policy?.IsSendable(template) ?? MetaSendable(template);
}
