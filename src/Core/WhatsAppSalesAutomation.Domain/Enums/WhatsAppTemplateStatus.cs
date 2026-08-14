namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// Mirrors Meta's real template review states. Without a live WhatsApp Business Account this is set
/// manually via the API rather than by an actual Meta review - see <c>MessageTemplateService</c>.
/// A campaign step may only reference an <see cref="Approved"/> template.
/// </summary>
public enum WhatsAppTemplateStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}
