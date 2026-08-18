namespace WhatsAppSalesAutomation.Application.Conversations;

public record ConversationDto(
    Guid Id,
    Guid CustomerId,
    string CustomerPhoneNumberE164,
    string CustomerName,
    string Mode,
    string Status,
    Guid? AssignedAgentId,
    DateTime? LastMessageAt,
    DateTime? LastInboundMessageAt,
    DateTime CreatedAt,
    DateTime? ClosedAt);

public record ConversationMessageDto(
    Guid Id,
    string Direction,
    string MessageType,
    string? Text,
    string? TemplateName,
    string Status,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    DateTime? ReadAt,
    DateTime CreatedAt);

public record ChangeConversationModeRequest(string Mode);

public record AssignConversationRequest(Guid AgentId);

/// <summary>
/// Exactly one of <paramref name="Text"/> or <paramref name="MessageTemplateId"/> must be given.
/// Free text is only accepted inside the customer service window (see
/// MessagingOptions.CustomerServiceWindowHours); a template works at any time, which is the entire
/// reason WhatsApp templates exist. A template's {{Placeholder}} tokens are resolved from the
/// customer automatically (same as a campaign step) rather than asking the agent to type parameter
/// values by hand.
/// </summary>
public record SendConversationMessageRequest(string? Text, Guid? MessageTemplateId);
