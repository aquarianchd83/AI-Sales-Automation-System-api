namespace WhatsAppSalesAutomation.Application.Onboarding;

/// <summary>One step of tenant onboarding: its place in the sequence, its weight towards the progress bar, and
/// the screen where the tenant does it.</summary>
public sealed record OnboardingStepDefinition(string Key, string Title, string Description, int Weight, string Route);

/// <summary>
/// The onboarding steps, in the order a tenant must complete them. Progress is the sum of the weights of the
/// completed steps, so the weights must add up to exactly 100 (a test holds this).
///
/// Each step is done on the tenant's real screen for it (the route) - onboarding reuses those screens rather
/// than duplicating them - and is checked against the data it produces (see OnboardingService).
/// </summary>
public static class OnboardingCatalog
{
    public const string Profile = "profile";
    public const string Plan = "plan";
    public const string CustomerPackage = "customer-package";
    public const string LeadDiscovery = "lead-discovery";
    public const string WhatsApp = "whatsapp";
    public const string MessageTemplate = "message-template";
    public const string Customer = "customer";
    public const string Campaign = "campaign";
    public const string KnowledgeBase = "knowledge-base";

    public static readonly IReadOnlyList<OnboardingStepDefinition> Steps = new OnboardingStepDefinition[]
    {
        new(Profile, "Profile Information",
            "Tell us about your business: name, industry, description, contact details and country.", 10, "/profile"),
        new(Plan, "Select Package Plan",
            "Choose the platform plan your business runs on.", 10, "/billing"),
        new(CustomerPackage, "Create Customer Package",
            "Create the package you will sell to your own customers, built on your plan.", 15, "/packages"),
        new(LeadDiscovery, "Lead Discovery Profile",
            "Describe the businesses you want to find: type, keywords and locations.", 10, "/lead-discovery/profile"),
        new(WhatsApp, "WhatsApp Configuration",
            "Connect your WhatsApp Business number and verify the connection.", 15, "/tenant-settings"),
        new(MessageTemplate, "Configure Message Template",
            "Create a WhatsApp message template and submit it for approval.", 10, "/message-templates"),
        new(Customer, "Create Customer",
            "Add your first customer.", 10, "/customers"),
        new(Campaign, "Create Campaign",
            "Create a campaign with at least one message step and one customer.", 10, "/campaigns"),
        new(KnowledgeBase, "Knowledge Base / Voucher",
            "Upload your voucher or brochure so the assistant can use it with customers.", 10, "/knowledge-base"),
    };
}
