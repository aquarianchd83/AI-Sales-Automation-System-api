using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One notice the platform sends over WhatsApp, with the template it starts out as.</summary>
/// <param name="SampleMessage">What <c>{{Message}}</c> looks like for this notice - the example Meta needs to review the template, and what a
/// test send shows.</param>
public record PlatformTemplateDefinition(
    TenantNotificationKind Kind,
    string Name,
    string WhatsAppTemplateName,
    string Body,
    string SampleMessage);

/// <summary>
/// The platform's WhatsApp notice templates as they are seeded, and the few values a template body may use. Seeding is insert-only
/// by kind: once a row exists the admin owns its wording, and a restart never overwrites it (the "Restore default" action is the way
/// back). The text is Utility - transactional, about the tenant's own account - and long enough around its two variables for Meta
/// to accept it: a template that is mostly variable is rejected.
/// </summary>
public static class PlatformTemplateCatalog
{
    public const string TenantNameToken = "TenantName";
    public const string TitleToken = "Title";
    public const string MessageToken = "Message";

    /// <summary>The values a notice supplies at send time. Anything else in a body is refused when it is saved.</summary>
    public static readonly IReadOnlyList<string> Tokens = new[] { TenantNameToken, TitleToken, MessageToken };

    public const string SampleTenantName = "Acme Traders";
    public const string SampleTitle = "Your plan renews soon";

    /// <summary>Meta's own sample template, present on every new Business Account - what the connection test sends.</summary>
    public const string ConnectionTestTemplate = "hello_world";
    public const string ConnectionTestLanguage = "en_US";

    /// <summary>Meta's limit for a template body.</summary>
    public const int MaxBodyLength = 1024;

    /// <summary>The longest a single value is allowed to be: leaves room for the fixed text around it within <see cref="MaxBodyLength"/>.</summary>
    public const int MaxValueLength = 600;

    private const string Closing = "Open the Billing page in your dashboard to take care of it.";

    public static readonly IReadOnlyList<PlatformTemplateDefinition> All = new[]
    {
        Define(TenantNotificationKind.QuotaLow20, "Quota running low (20% left)", "tenant_alert_quota_low_20",
            "Hi {{TenantName}}, your balance is running low and about a fifth of it is left. {{Message}} Top up from the Billing page in your dashboard so campaigns and AI replies keep going without a break.",
            "You have 200 of 1,000 WhatsApp messages left."),

        Define(TenantNotificationKind.QuotaLow5, "Quota almost used up (5% left)", "tenant_alert_quota_low_5",
            "Hi {{TenantName}}, your balance is almost used up and only a few units remain. {{Message}} Top up now from the Billing page in your dashboard, otherwise sending will pause when it reaches zero.",
            "You have 50 of 1,000 WhatsApp messages left."),

        Define(TenantNotificationKind.QuotaExhausted, "Quota used up", "tenant_alert_quota_exhausted",
            "Hi {{TenantName}}, you have used all of your balance, so sending has paused. {{Message}} Buy credits or renew your plan from the Billing page in your dashboard and sending resumes by itself.",
            "Your WhatsApp messages balance is now 0."),

        Define(TenantNotificationKind.CreditsExpiring14, "Credits expiring in 14 days", "tenant_alert_credits_expiring_14",
            "Hi {{TenantName}}, some of the credits you bought will expire in the next two weeks. {{Message}} Use them before they lapse, because expired credits cannot be restored.",
            "5,000 of your purchased WhatsApp messages credits expire in the next 14 days, on 24 Oct 2026."),

        Define(TenantNotificationKind.CreditsExpiring3, "Credits expiring in 3 days", "tenant_alert_credits_expiring_3",
            "Hi {{TenantName}}, some of the credits you bought will expire within three days. {{Message}} Use them before they lapse, because expired credits cannot be restored.",
            "5,000 of your purchased WhatsApp messages credits expire in the next 3 days, on 13 Oct 2026."),

        Define(TenantNotificationKind.CreditsAdded, "Credits added", "tenant_alert_credits_added",
            "Hi {{TenantName}}, good news: units have been added to your account. {{Message}} You can see your balance on the Billing page in your dashboard.",
            "1,000 WhatsApp messages were added to your account."),

        Define(TenantNotificationKind.RefundApproved, "Refund approved", "tenant_alert_refund_approved",
            "Hi {{TenantName}}, we have reviewed your refund request and approved it. {{Message}} The refund is being processed to your original payment method.",
            "Your refund of USD 16.00 was approved."),

        Define(TenantNotificationKind.RefundRejected, "Refund rejected", "tenant_alert_refund_rejected",
            "Hi {{TenantName}}, we have reviewed your refund request and could not approve it. {{Message}} You can see the details on the Billing page in your dashboard or reply to this message for help.",
            "Your refund request was not approved."),

        Define(TenantNotificationKind.RefundExpired, "Refund request expired", "tenant_alert_refund_expired",
            "Hi {{TenantName}}, your refund request closed without a decision. {{Message}} The units that were held for it are back in your balance, and you can ask again from the Billing page.",
            "Your refund request expired after 14 days."),

        Define(TenantNotificationKind.PlanExpiring7, "Plan expiring in 7 days", "tenant_alert_plan_expiring_7",
            "Hi {{TenantName}}, your plan's billing period ends in about a week. {{Message}} " + Closing,
            "Your Growth plan ends on 10 Oct 2026 and renews for USD 99.00."),

        Define(TenantNotificationKind.PlanExpiring1, "Plan expiring tomorrow", "tenant_alert_plan_expiring_1",
            "Hi {{TenantName}}, your plan's billing period ends tomorrow. {{Message}} " + Closing,
            "Your Growth plan ends on 4 Oct 2026 and renews for USD 99.00."),

        Define(TenantNotificationKind.PlanRenewalFailed, "Plan expired - renewal failed", "tenant_alert_plan_expired",
            "Hi {{TenantName}}, your plan has ended and could not be renewed, so no new balance was added. {{Message}} Please contact support or check the Billing page in your dashboard.",
            "Your Growth plan ended on 4 Oct 2026 but can't renew automatically right now."),
    };

    public static PlatformTemplateDefinition? For(string eventKey) =>
        All.FirstOrDefault(d => string.Equals(d.Kind.ToString(), eventKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the example Meta needs for each token of the template.</summary>
    public static IReadOnlyDictionary<string, string> Examples(string sampleMessage) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TenantNameToken] = SampleTenantName,
            [TitleToken] = SampleTitle,
            [MessageToken] = sampleMessage
        };

    /// <summary>The tokens of a body that a notice cannot fill.</summary>
    public static IReadOnlyList<string> UnknownTokens(string bodyText) =>
        Common.TemplatePlaceholderResolver.ExtractTokens(bodyText)
            .Where(t => !Tokens.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Meta refuses a template parameter that is empty, holds a line break or tab, or has four spaces in a row - and a notice's
    /// text comes from emails, where all three are normal. Folds all whitespace to single spaces and caps the length.</summary>
    public static string CleanValue(string? value, string fallback)
    {
        var cleaned = Whitespace.Replace(value ?? string.Empty, " ").Trim();
        if (cleaned.Length == 0)
            return fallback;

        return cleaned.Length <= MaxValueLength ? cleaned : cleaned[..(MaxValueLength - 1)].TrimEnd() + "…";
    }

    private static PlatformTemplateDefinition Define(TenantNotificationKind kind, string name, string whatsAppName, string body, string sampleMessage) =>
        new(kind, name, whatsAppName, body, sampleMessage);
}
