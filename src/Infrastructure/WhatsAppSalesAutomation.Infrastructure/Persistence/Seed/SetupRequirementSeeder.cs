using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Setup;
using WhatsAppSalesAutomation.Domain.Entities.Setup;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Gives each starter plan a published version 1 of its setup requirements, so a fresh install has working
/// applications from the first boot. Insert-only and idempotent, like <see cref="PlanSeeder"/>: a plan that already
/// has ANY setup version (including one an admin built or edited) is never touched again - from then on its
/// requirements are managed through the Platform Admin Console's Setup Plans screen, not this file.
///
/// The three catalogues deliberately build on each other (Starter -> Growth keeps every Starter question and adds
/// lead-generation ones), which is what lets a Talent move up a plan and only be asked the new questions.
/// </summary>
public static class SetupRequirementSeeder
{
    private const string S = SetupSections.Business, A = SetupSections.Audience, P = SetupSections.Product,
        C = SetupSections.Campaign, M = SetupSections.Communication;

    private sealed record Spec(
        string Key, string Label, SetupFieldType Type, string Section, bool Required = false, string? Help = null,
        string[]? Options = null, string? WhenKey = null, SetupConditionOperator WhenOp = SetupConditionOperator.Equals,
        string? WhenValue = null, string? Default = null, string? Metric = null, SetupValidationDto? Rules = null);

    private static string[] Opts(params string[] pairs) => pairs;

    // ---- Basic social media marketing -------------------------------------------------------------------------
    private static IEnumerable<Spec> Basic() => new[]
    {
        new Spec("brand_name", "Business / brand name", SetupFieldType.Text, S, true, "As customers know it."),
        new Spec("business_description", "Business description", SetupFieldType.MultilineText, S, true,
            "What you do and what makes you different, in a few sentences.", Rules: new(null, null, 20, 1000, null, null)),
        new Spec("website", "Website", SetupFieldType.Url, S, false, "Include https://"),
        new Spec("logo", "Logo", SetupFieldType.FileUpload, S, false, "PNG or JPG, used on posts and creatives."),

        new Spec("target_audience", "Target audience", SetupFieldType.MultilineText, A, true,
            "Who should see your content? Age, interests, where they live, what they need."),

        new Spec("social_platforms", "Social media platforms", SetupFieldType.MultiSelect, C, true, "Pick every platform you want us to post on.",
            Opts("instagram:Instagram", "facebook:Facebook", "linkedin:LinkedIn", "youtube:YouTube", "x:X (Twitter)")),
        new Spec("instagram_handle", "Instagram handle", SetupFieldType.Text, C, true, "For example @yourbrand",
            WhenKey: "social_platforms", WhenOp: SetupConditionOperator.Contains, WhenValue: "instagram"),
        new Spec("facebook_page_url", "Facebook page link", SetupFieldType.Url, C, true, "The full address of your Facebook page.",
            WhenKey: "social_platforms", WhenOp: SetupConditionOperator.Contains, WhenValue: "facebook"),
        new Spec("marketing_objective", "Marketing objective", SetupFieldType.Dropdown, C, true, null,
            Opts("brand_awareness:Build brand awareness", "engagement:Grow engagement", "traffic:Drive website traffic", "sales:Increase sales")),
        new Spec("content_frequency", "Content frequency", SetupFieldType.Dropdown, C, true, "How often should we publish?",
            Opts("daily:Every day", "three_weekly:3 times a week", "weekly:Once a week"), Default: "three_weekly"),
        new Spec("content_types", "Preferred content type", SetupFieldType.MultiSelect, C, true, null,
            Opts("images:Images", "videos:Videos", "reels:Reels / Shorts", "stories:Stories", "carousels:Carousels", "text:Text posts")),

        new Spec("run_paid_ads", "Do you want to run paid advertising?", SetupFieldType.Radio, C, true, null,
            Opts("yes:Yes", "no:No"), Default: "no"),
        new Spec("ad_platform", "Advertising platform", SetupFieldType.Dropdown, C, true, null,
            Opts("meta:Facebook & Instagram Ads", "google:Google Ads", "linkedin:LinkedIn Ads", "youtube:YouTube Ads"),
            "run_paid_ads", SetupConditionOperator.Equals, "yes"),
        new Spec("ad_monthly_budget", "Monthly ad budget", SetupFieldType.Currency, C, true, "What you are happy to spend on ads each month.",
            WhenKey: "run_paid_ads", WhenValue: "yes", Metric: SetupMetrics.SocialMediaCost),
        new Spec("ad_target_location", "Ad target location", SetupFieldType.Text, C, true, "City, region or country.",
            WhenKey: "run_paid_ads", WhenValue: "yes"),
        new Spec("ad_age_group", "Ad target age group", SetupFieldType.Dropdown, C, false, null,
            Opts("all:All ages", "18_24:18 - 24", "25_34:25 - 34", "35_44:35 - 44", "45_54:45 - 54", "55_plus:55+"),
            "run_paid_ads", SetupConditionOperator.Equals, "yes", Default: "all"),
        new Spec("ad_target_gender", "Ad target gender", SetupFieldType.Radio, C, false, null,
            Opts("all:Everyone", "female:Women", "male:Men"), "run_paid_ads", SetupConditionOperator.Equals, "yes", Default: "all"),

        new Spec("contact_email", "Contact email", SetupFieldType.Email, M, true, "Where we send campaign reports."),
        new Spec("contact_phone", "Contact phone", SetupFieldType.Phone, M, false, "Include the country code, e.g. +91 98765 43210."),
    };

    // ---- Lead generation: everything above, plus ------------------------------------------------------------------
    private static IEnumerable<Spec> LeadGeneration() => Basic().Concat(new[]
    {
        new Spec("target_location", "Target location", SetupFieldType.MultilineText, A, true, "Cities or regions to find leads in, e.g. Chandigarh, Mohali, Panchkula."),
        new Spec("industry", "Industry", SetupFieldType.Text, A, true, "The industry your ideal customers work in."),
        new Spec("target_customer_type", "Target customer type", SetupFieldType.Dropdown, A, true, null,
            Opts("smb:Small & medium businesses", "enterprise:Large enterprises", "startups:Startups", "consumers:Individual consumers")),

        new Spec("lead_criteria", "Lead criteria", SetupFieldType.MultilineText, C, true, "What makes someone a good lead for you?"),
        new Spec("lead_source", "Lead source", SetupFieldType.MultiSelect, C, true, null,
            Opts("internet_search:Internet search", "social_media:Social media", "directories:Business directories", "referrals:Referrals", "events:Events")),
        new Spec("lead_qualification_rules", "Lead qualification rules", SetupFieldType.MultilineText, C, true,
            "When should a lead count as qualified? e.g. budget above 10,000 and ready within 30 days."),
        new Spec("campaign_budget", "Campaign budget (monthly)", SetupFieldType.Currency, C, true, null, Metric: SetupMetrics.MarketingCost),
        new Spec("expected_leads", "Leads you hope to get each month", SetupFieldType.Number, C, false, "Helps us estimate your return.",
            Metric: SetupMetrics.ExpectedLeads, Rules: new(0, 1_000_000, null, null, null, null)),
        new Spec("expected_customers", "Customers you hope to win each month", SetupFieldType.Number, C, false, "Helps us estimate your return.",
            Metric: SetupMetrics.ExpectedCustomers, Rules: new(0, 1_000_000, null, null, null, null)),
        new Spec("package_price", "Average price per customer", SetupFieldType.Currency, C, false, "What one customer pays you.", Metric: SetupMetrics.PackagePrice),

        new Spec("lead_channels", "How should we contact leads?", SetupFieldType.MultiSelect, M, true, null,
            Opts("whatsapp:WhatsApp", "email:Email")),
    });

    // ---- AI sales automation: a full sales setup ---------------------------------------------------------------------
    private static IEnumerable<Spec> SalesAutomation() => new[]
    {
        new Spec("brand_name", "Business / brand name", SetupFieldType.Text, S, true),
        new Spec("business_description", "Business description", SetupFieldType.MultilineText, S, true,
            "What you do and what makes you different.", Rules: new(null, null, 20, 1000, null, null)),
        new Spec("website", "Website", SetupFieldType.Url, S, false, "Include https://"),
        new Spec("logo", "Logo", SetupFieldType.FileUpload, S, false),

        new Spec("target_audience", "Target audience", SetupFieldType.MultilineText, A, true, "Who do you sell to?"),
        new Spec("target_location", "Target location", SetupFieldType.MultilineText, A, true, "Cities or regions you sell in."),
        new Spec("industry", "Industry", SetupFieldType.Text, A, true),
        new Spec("target_customer_type", "Target customer type", SetupFieldType.Dropdown, A, true, null,
            Opts("smb:Small & medium businesses", "enterprise:Large enterprises", "startups:Startups", "consumers:Individual consumers")),

        new Spec("product_description", "Product / service information", SetupFieldType.MultilineText, P, true, "What you sell, and its main benefits."),
        new Spec("pricing", "Pricing", SetupFieldType.MultilineText, P, true, "Your prices, plans and any discounts the assistant may mention."),
        new Spec("package_price", "Average price per customer", SetupFieldType.Currency, P, false, "Used to estimate your revenue.", Metric: SetupMetrics.PackagePrice),
        new Spec("faqs", "Frequently asked questions", SetupFieldType.MultilineText, P, true, "Questions customers ask, with your answers."),
        new Spec("knowledge_base_file", "Knowledge base document", SetupFieldType.FileUpload, P, false, "A brochure or price list the assistant can learn from."),
        new Spec("sales_pitch", "Sales pitch", SetupFieldType.MultilineText, P, true, "How you would pitch to a new customer, in your own words."),

        new Spec("lead_qualification_criteria", "Lead qualification criteria", SetupFieldType.MultilineText, C, true, "When is a lead ready to be handed to your team?"),
        new Spec("followup_rules", "Follow-up rules", SetupFieldType.MultilineText, C, true, "When and how should we follow up if a lead goes quiet?"),
        new Spec("followup_delay_hours", "First follow-up after (hours)", SetupFieldType.Number, C, true, null,
            Default: "24", Rules: new(1, 720, null, null, null, null)),
        new Spec("campaign_name", "Campaign name", SetupFieldType.Text, C, true),
        new Spec("campaign_budget", "Campaign budget (monthly)", SetupFieldType.Currency, C, true, null, Metric: SetupMetrics.MarketingCost),
        new Spec("operational_cost", "Other monthly costs", SetupFieldType.Currency, C, false, "Tools, staff time or anything else this costs you.", Metric: SetupMetrics.OperationalCost),
        new Spec("expected_leads", "Leads you hope to get each month", SetupFieldType.Number, C, false, null, Metric: SetupMetrics.ExpectedLeads),
        new Spec("expected_customers", "Customers you hope to win each month", SetupFieldType.Number, C, false, null, Metric: SetupMetrics.ExpectedCustomers),

        new Spec("communication_channels", "Communication channels", SetupFieldType.MultiSelect, M, true, null,
            Opts("whatsapp:WhatsApp", "email:Email", "sms:SMS", "phone:Phone calls")),
        new Spec("whatsapp_number", "WhatsApp number", SetupFieldType.Phone, M, true, "The number customers will message.",
            WhenKey: "communication_channels", WhenOp: SetupConditionOperator.Contains, WhenValue: "whatsapp"),
        new Spec("sender_email", "Sender email address", SetupFieldType.Email, M, true, null,
            WhenKey: "communication_channels", WhenOp: SetupConditionOperator.Contains, WhenValue: "email"),
        new Spec("business_hours", "Business hours", SetupFieldType.Text, M, false, "When your team can take over a conversation.", Default: "Mon-Sat, 9am-6pm"),
    };

    private static readonly (string PlanCode, Func<IEnumerable<Spec>> Build)[] Catalogue =
    {
        ("starter", Basic),
        ("growth", LeadGeneration),
        ("scale", SalesAutomation),
    };

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var plans = await db.Plans.ToListAsync();
        var planIdsWithVersions = (await db.PlanSetupVersions.Select(v => v.PlanId).Distinct().ToListAsync()).ToHashSet();
        var changed = false;

        foreach (var (code, build) in Catalogue)
        {
            var plan = plans.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
            if (plan is null || planIdsWithVersions.Contains(plan.Id))
                continue;

            var version = new PlanSetupVersion
            {
                PlanId = plan.Id,
                VersionNumber = 1,
                Status = SetupVersionStatus.Published,
                ReleaseNotes = "Initial setup requirements.",
                PublishedAt = DateTime.UtcNow
            };
            db.PlanSetupVersions.Add(version);

            var order = new Dictionary<string, int>();
            foreach (var spec in build())
            {
                var next = order.GetValueOrDefault(spec.Section) + 10;
                order[spec.Section] = next;

                db.PlanRequirements.Add(new PlanRequirement
                {
                    PlanSetupVersionId = version.Id,
                    FieldKey = spec.Key,
                    Label = spec.Label,
                    HelpText = spec.Help,
                    FieldType = spec.Type,
                    IsRequired = spec.Required,
                    DefaultValue = spec.Default,
                    OptionsJson = SetupJson.WriteOptions(spec.Options?.Select(ToOption).ToList()),
                    ValidationJson = SetupJson.WriteValidation(spec.Rules),
                    // Sections are ordered by their position in the wizard; fields keep their authoring order within one.
                    DisplayOrder = SectionRank(spec.Section) * 1000 + next,
                    Section = spec.Section,
                    ConditionFieldKey = spec.WhenKey,
                    ConditionOperator = spec.WhenKey is null ? null : spec.WhenOp,
                    ConditionValue = spec.WhenKey is null ? null : spec.WhenValue,
                    MetricKey = spec.Metric,
                    IsActive = true
                });
            }

            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static int SectionRank(string section) => SetupSections.Describe(section).Order;

    private static SetupOptionDto ToOption(string pair)
    {
        var split = pair.IndexOf(':');
        return new SetupOptionDto(pair[..split], pair[(split + 1)..]);
    }
}
