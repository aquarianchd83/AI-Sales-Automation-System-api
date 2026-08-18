namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Pipeline stage of a Lead. Forward-moving in the normal case (New -> ... -> Won/Lost), but
/// nothing in the domain enforces monotonicity - a Negotiation can slip back to Qualifying, an agent
/// can correct a mistaken stage - so this is a plain settable property, not a state machine.</summary>
public enum LeadStage
{
    New = 0,
    Qualifying = 1,
    Qualified = 2,
    Negotiation = 3,
    Won = 4,
    Lost = 5
}
