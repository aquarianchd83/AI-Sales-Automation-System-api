namespace WhatsAppSalesAutomation.Application.Handoffs;

public record HandoffDto(
    Guid Id,
    Guid ConversationId,
    Guid CustomerId,
    string CustomerPhoneNumberE164,
    string CustomerName,
    string TriggerReason,
    string Status,
    Guid? AssignedAgentId,
    DateTime? AssignedAt,
    DateTime? ResolvedAt,
    string? Notes,
    DateTime CreatedAt);

public record ResolveHandoffRequest(string? Notes);
