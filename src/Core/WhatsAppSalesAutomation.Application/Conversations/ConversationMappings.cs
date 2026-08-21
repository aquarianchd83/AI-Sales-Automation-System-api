using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;

namespace WhatsAppSalesAutomation.Application.Conversations;

public static class ConversationMappings
{
    public static ConversationDto ToDto(this Conversation conversation, Customer customer) => new(
        conversation.Id,
        conversation.CustomerId,
        customer.PhoneNumberE164,
        customer.FullName,
        conversation.Mode.ToString(),
        conversation.Status.ToString(),
        conversation.AssignedAgentId,
        conversation.LastMessageAt,
        conversation.LastInboundMessageAt,
        conversation.CreatedAt,
        conversation.ClosedAt,
        conversation.AiConfidenceLast,
        conversation.LastDetectedIntent,
        conversation.LastLeadScore?.ToString(),
        conversation.Summary);

    public static ConversationMessageDto ToDto(this Message message) => new(
        message.Id,
        message.Direction.ToString(),
        message.MessageType.ToString(),
        message.Text,
        message.TemplateName,
        message.Status.ToString(),
        message.SentAt,
        message.DeliveredAt,
        message.ReadAt,
        message.CreatedAt);
}
