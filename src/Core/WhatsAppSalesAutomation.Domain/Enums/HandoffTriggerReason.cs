namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// LowConfidence/CannotAnswer/ComplexTechnical are Phase 5 values - there is no AI service to produce
/// a confidence score or judge complexity yet, so Phase 4 only ever raises <see cref="RuleTriggered"/>
/// (every inbound message, since there is no AI to attempt a reply first) or
/// <see cref="CustomerRequested"/> (an explicit "talk to a human"-style message, once Phase 5's intent
/// detection exists to recognise one). The rest of the enum is kept now so Phase 5 does not need a
/// migration just to start using it.
/// </summary>
public enum HandoffTriggerReason
{
    CustomerRequested = 0,
    LowConfidence = 1,
    CannotAnswer = 2,
    Complaint = 3,
    Negotiation = 4,
    ComplexTechnical = 5,
    RuleTriggered = 6
}
