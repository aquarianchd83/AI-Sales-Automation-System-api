namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>Refund rules, bound from "Billing:Refunds". Policy, not code: change the numbers, not the service.</summary>
public class RefundPolicyOptions
{
    /// <summary>Unused credits are refundable this long after purchase.</summary>
    public int CreditWindowDays { get; set; } = 30;

    /// <summary>A subscription is refundable this long after it was charged...</summary>
    public int SubscriptionWindowDays { get; set; } = 7;

    /// <summary>...and only if the tenant has used less than this share of every quota it included.</summary>
    public decimal SubscriptionMaxUsageFraction { get; set; } = 0.10m;

    /// <summary>A subscription refund is prorated over a period of this many days.</summary>
    public int SubscriptionPeriodDays { get; set; } = 30;

    /// <summary>A request nobody answers is closed after this long and its held units returned.</summary>
    public int RequestExpiryDays { get; set; } = 14;
}

/// <summary>Billing alert delivery, bound from "Billing:Alerts".</summary>
public class BillingAlertOptions
{
    /// <summary>The approved Utility template the platform WhatsApp number sends alerts with. It takes two
    /// body parameters: the tenant's name, then the message. It has to be created and approved in Meta first.</summary>
    public string WhatsAppTemplateName { get; set; } = "billing_alert";

    public string WhatsAppTemplateLanguage { get; set; } = "en";
}
