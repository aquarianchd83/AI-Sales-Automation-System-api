namespace WhatsAppSalesAutomation.Application.Setup;

/// <summary>The recommended wizard steps, in order. A requirement may use any section key; one that is not listed
/// here is titled from its key and sorted after these, so adding a step needs no code change.</summary>
public static class SetupSections
{
    public const string Business = "business";
    public const string Audience = "audience";
    public const string Product = "product";
    public const string Campaign = "campaign";
    public const string Communication = "communication";

    private static readonly (string Key, string Title, string Description)[] Known =
    {
        (Business, "Business Information", "Tell us about your business or brand."),
        (Audience, "Target Audience", "Who are you trying to reach?"),
        (Product, "Product / Service", "What are you promoting or selling?"),
        (Campaign, "Marketing & Campaign", "How should the campaign run?"),
        (Communication, "Communication", "Where and how should we talk to your customers?"),
    };

    public static (string Title, string Description, int Order) Describe(string key)
    {
        for (var i = 0; i < Known.Length; i++)
        {
            if (string.Equals(Known[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return (Known[i].Title, Known[i].Description, i);
        }

        var words = key.Replace('_', ' ').Replace('-', ' ').Trim();
        var title = words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
        return (title, string.Empty, 100);
    }

    public static IReadOnlyList<(string Key, string Title, string Description, int Order)> All =>
        Known.Select((k, i) => (k.Key, k.Title, k.Description, i)).ToList();
}

/// <summary>
/// Answers a requirement can be tagged with (<c>PlanRequirement.MetricKey</c>) so the plan's own configuration
/// feeds the "what can I expect from this plan" projection: customers x price = revenue, minus the costs, = profit.
/// </summary>
public static class SetupMetrics
{
    public const string PackagePrice = "package_price";
    public const string ExpectedCustomers = "expected_customers";
    public const string ExpectedLeads = "expected_leads";
    public const string MarketingCost = "marketing_cost";
    public const string SocialMediaCost = "social_media_cost";
    public const string OperationalCost = "operational_cost";

    public static readonly IReadOnlyList<string> All = new[]
    {
        PackagePrice, ExpectedCustomers, ExpectedLeads, MarketingCost, SocialMediaCost, OperationalCost
    };
}
