namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>The outcome a tenant's AI sales agent steers conversations toward. Per tenant rather than
/// per conversation: a business has one primary conversion action, and an agent offering a customer
/// three different next steps is how a sales conversation stalls.</summary>
public enum ConversationGoal
{
    /// <summary>Default. Answer well and capture the lead; no specific closing action.</summary>
    Enquiry = 0,

    Purchase = 1,

    Demo = 2,

    Appointment = 3,

    SiteVisit = 4,

    Consultation = 5,

    Registration = 6
}
