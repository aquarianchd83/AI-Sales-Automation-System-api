namespace WhatsAppSalesAutomation.Application.MessageTemplates;

public record MessageTemplateDto(
    Guid Id,
    string Name,
    string Language,
    string Category,
    string WhatsAppTemplateName,
    string WhatsAppTemplateStatus,
    string BodyText,
    bool IsActive,
    DateTime CreatedAt);

public record CreateMessageTemplateRequest(
    string Name,
    string Language,
    string Category,
    string WhatsAppTemplateName,
    string BodyText);

public record UpdateMessageTemplateRequest(string BodyText, bool IsActive);

/// <summary>
/// Stands in for Meta's real template review, which needs a live WhatsApp Business Account this
/// system does not have configured yet. A SuperAdmin sets the outcome directly instead.
/// </summary>
public record ReviewMessageTemplateRequest(string Status);
