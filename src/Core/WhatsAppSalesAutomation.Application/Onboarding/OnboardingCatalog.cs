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
    public const string KnowledgeBase = "knowledge-base";

    /// <summary>TEMPORARY: steps that stay to do but no longer hold up the step after them, so the flow can be walked
    /// past one still being worked on. Empty this set to restore strict sequencing.</summary>
    public static readonly IReadOnlySet<string> OnHold = new HashSet<string>();

    public static readonly IReadOnlyList<OnboardingStepDefinition> Steps = new OnboardingStepDefinition[]
    {
        new(Profile, "Profile Information",
            "Tell us about your business: name, industry, description, contact details and country.", 15, "/profile"),
        new(Plan, "Select Package Plan",
            "Choose the plan your business runs on - or carry on with your free trial and choose later.", 15, "/billing"),
        new(CustomerPackage, "Create Customer Package",
            "Create the package you will sell to your own customers, built on your plan.", 20, "/packages"),
        new(WhatsApp, "WhatsApp Configuration",
            "Tell us the WhatsApp number your customers will message. Connecting it to WhatsApp comes later.", 20, "/tenant-settings"),
        new(LeadDiscovery, "Lead Discovery Profile",
            "Describe the businesses you want to find: type, keywords and locations.", 15, "/lead-discovery/profile"),
        new(KnowledgeBase, "Knowledge Base / Voucher",
            "Upload your voucher or brochure so the assistant can use it with customers.", 15, "/knowledge-base"),
    };
}
