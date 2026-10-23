using System.Text.Json.Serialization;

namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// The product areas a plan can include and a tenant can cap. One credit wallet pays for all of them; what a feature has spent is kept in its own
/// ledger (every ledger entry carries its feature). Stored as a string, so extending this never renumbers a row.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlatformFeature
{
    /// <summary>Template messages sent by an agent, a campaign or a lead follow-up.</summary>
    WhatsAppMessaging = 0,

    /// <summary>The AI answering customers in WhatsApp.</summary>
    AiConversations = 1,

    /// <summary>Finding new leads for the tenant's business.</summary>
    LeadDiscovery = 2,

    /// <summary>AI content generation and video rendering.</summary>
    ContentStudio = 3,

    /// <summary>Reading sources and drafting product knowledge.</summary>
    ProductKnowledge = 4
}
