namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>The platform module a ticket or article is about. Mirrors the Application layer's bounded
/// contexts, so a module detected on a ticket maps directly onto the services that own it - which is
/// what lets the support agent's tool pre-selection be deterministic rather than a model guess.
///
/// NULL on an article means cross-cutting (a billing policy that is not module-specific), which is a
/// different statement from "unknown" and is why the property is nullable rather than carrying an
/// Unknown member.</summary>
public enum ProductModule
{
    Platform = 0,
    Billing = 1,
    Quota = 2,
    Ai = 3,
    WhatsApp = 4,
    MessageTemplates = 5,
    Campaigns = 6,
    Conversations = 7,
    Handoffs = 8,
    Leads = 9,
    LeadDiscovery = 10,
    Customers = 11,
    Media = 12,
    Users = 13,
    Settings = 14,
    Notifications = 15,
    Reports = 16
}
