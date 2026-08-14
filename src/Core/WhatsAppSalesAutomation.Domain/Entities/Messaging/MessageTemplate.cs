using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Messaging;

/// <summary>
/// A WhatsApp message template. Business-initiated messages - which is what every campaign send is -
/// must always use an approved template, regardless of the 24-hour customer service window; that
/// window only governs free-form replies to a customer who messaged first, which is Phase 4/5 scope.
/// </summary>
public class MessageTemplate : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public string Language { get; set; } = "en";

    public TemplateCategory Category { get; set; } = TemplateCategory.Marketing;

    /// <summary>The name registered with Meta - what actually goes in the send API call.</summary>
    public string WhatsAppTemplateName { get; set; } = string.Empty;

    public WhatsAppTemplateStatus WhatsAppTemplateStatus { get; set; } = WhatsAppTemplateStatus.Pending;

    /// <summary>
    /// Body text with <c>{{Placeholder}}</c> tokens, e.g. "Hi {{FirstName}}, ...". Positional values
    /// are resolved from the customer at send time by <c>Application.Common.TemplatePlaceholderResolver</c>.
    /// </summary>
    public string BodyText { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
