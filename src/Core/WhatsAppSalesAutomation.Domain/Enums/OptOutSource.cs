namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>How a customer's opt-out was detected. Compliance questions ("did they really ask us to
/// stop?") need the answer, and an <see cref="AiDetected"/> opt-out is the one worth spot-checking -
/// the other three are deterministic.</summary>
public enum OptOutSource
{
    /// <summary>Whole message matched a known opt-out keyword exactly, e.g. "STOP".</summary>
    ExactKeyword = 0,

    /// <summary>Message matched a configured opt-out phrase pattern, e.g. "don't message me".</summary>
    PhrasePattern = 1,

    /// <summary>The AI reported the customer asked to stop. Kept distinct because it is the only
    /// non-deterministic path - see the opt-out section of the Phase 7 design doc for why trusting the
    /// model here is the safe direction.</summary>
    AiDetected = 2,

    /// <summary>A human set it - an agent on the customer record, or an import.</summary>
    Manual = 3
}
