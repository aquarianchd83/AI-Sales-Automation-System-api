namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// What the customer currently wants, as the AI sales agent reads it. Replaces the free-text intent
/// the model used to return, whose only constraint was a suggestion of six example labels in the
/// prompt - meaning two turns (or two providers) could produce different strings for the same thing,
/// and <c>AiOptions.EscalationIntents</c> could silently fail to match any of them.
///
/// <c>Conversation.LastDetectedIntent</c> and <c>AiInteraction.DetectedIntent</c> stay string columns,
/// so adopting this needs no data migration - only the values written into them become constrained.
/// Values the model returns that do not parse become <see cref="Unknown"/> rather than being stored
/// verbatim, so a provider that ignores the schema cannot widen the taxonomy by accident.
/// </summary>
public enum CustomerIntent
{
    /// <summary>A general question about the business that is not about a specific product or price.</summary>
    Information = 0,

    PriceEnquiry = 1,

    ProductEnquiry = 2,

    ServiceEnquiry = 3,

    /// <summary>Weighing this business against an alternative - a buying signal, but not yet a
    /// decision.</summary>
    Comparison = 4,

    /// <summary>Positive engagement without a concrete next step yet.</summary>
    Interested = 5,

    /// <summary>Answering a qualification question rather than asking anything.</summary>
    Qualification = 6,

    PurchaseIntent = 7,

    DemoRequest = 8,

    AppointmentRequest = 9,

    SiteVisit = 10,

    Booking = 11,

    /// <summary>A problem with something already bought. Distinct from Complaint: this one is
    /// answerable, a complaint is a relationship issue.</summary>
    Support = 12,

    Complaint = 13,

    HumanRequest = 14,

    NotInterested = 15,

    /// <summary>Asked to stop receiving messages, in any wording. Acted on by the opt-out path
    /// (see <c>InboundWebhookProcessor</c>), never merely noted.</summary>
    OptOut = 16,

    /// <summary>Negotiating terms or price on something already scoped. Not in the original prompt's
    /// list, but the existing handoff mapping already routes it to
    /// <see cref="HandoffTriggerReason.Negotiation"/>, and dropping it would silently retire a
    /// working escalation path and the seeded scoring rule that depends on it.</summary>
    Negotiation = 17,

    /// <summary>The model could not classify the message, or returned a value outside this enum.
    /// Never a reason to answer confidently.</summary>
    Unknown = 18
}
