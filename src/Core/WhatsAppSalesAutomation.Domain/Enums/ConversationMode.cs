namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// Who talks to the customer. <c>AI</c>: the AI replies on its own and escalates when it should not.
/// <c>Human</c>: the AI never runs; the agent writes every reply. <c>Hybrid</c>: the AI answers plain
/// questions itself, but wherever it would escalate - and while a handoff is open - its reply is held as
/// a draft for the agent to send, edit or discard (see ConversationOrchestrator).
/// </summary>
public enum ConversationMode
{
    AI = 0,
    Human = 1,
    Hybrid = 2
}
