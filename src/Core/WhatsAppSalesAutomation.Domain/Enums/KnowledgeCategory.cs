namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Coarse bucket for retrieval pre-filtering and admin navigation. Replaces the free-text
/// Category string, which could not be filtered on reliably because every author spelled it
/// differently. Finer distinctions live in KnowledgeBaseArticle.SubCategory, which stays free text on
/// purpose - subcategories proliferate with the product and are not worth a migration each.</summary>
public enum KnowledgeCategory
{
    GettingStarted = 0,
    Billing = 1,
    Subscription = 2,
    AiUsage = 3,
    WhatsApp = 4,
    Campaigns = 5,
    LeadDiscovery = 6,
    Conversations = 7,
    Crm = 8,
    Templates = 9,
    Integrations = 10,
    Security = 11,
    Policy = 12,
    Troubleshooting = 13,
    ReleaseNotes = 14
}
