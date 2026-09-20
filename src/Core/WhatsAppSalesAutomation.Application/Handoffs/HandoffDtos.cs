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
    DateTime CreatedAt,
    /// <summary>The briefing assembled when this handoff was raised, or null for one raised before
    /// briefings existed (or by a path that does not build them, such as the webhook processor's
    /// "no AI attempted this" handoff). Deserialized here rather than handed to the client as a JSON
    /// string, so the agent screen reads a typed object like every other field on this DTO.</summary>
    HandoffSummary? Summary);

public record ResolveHandoffRequest(string? Notes);
