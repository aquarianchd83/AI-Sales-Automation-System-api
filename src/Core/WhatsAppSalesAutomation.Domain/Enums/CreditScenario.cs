namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Every situation in which the application spends credits. Stored by name, so a value can be added but never renamed.
/// Each one has a payer setting on the Credit Payers screen: the tenant's own balance, or the platform's.</summary>
public enum CreditScenario
{
    /// <summary>The AI answering a customer in WhatsApp.</summary>
    CustomerChatReply = 0,

    /// <summary>A billable WhatsApp template send: an agent, a campaign or a lead follow-up.</summary>
    WhatsAppTemplateSend = 1,

    /// <summary>Candidates evaluated by a lead discovery run.</summary>
    LeadDiscovery = 2,

    /// <summary>An AI Content Studio generation step: ideas, a script, a scene breakdown, a voice-over, an illustration.</summary>
    ContentStudioGeneration = 3,

    /// <summary>An AI Content Studio video render.</summary>
    ContentStudioVideoRender = 4,

    /// <summary>Product knowledge: reading what a tenant uploaded and building the knowledge from it (documents, scans, websites).</summary>
    ProductKnowledgeReading = 5,

    /// <summary>Product knowledge: drafting, checking and answering questions from the sources.</summary>
    ProductKnowledgeAnswering = 6
}
