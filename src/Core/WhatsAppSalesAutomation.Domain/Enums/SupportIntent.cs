namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What a support ticket is asking for (§K.2). A fixed taxonomy on purpose: the classifier
/// chooses from this list rather than inventing labels, so every downstream decision - which module
/// to boost, whether the agent may answer at all - is a lookup rather than an interpretation.</summary>
public enum SupportIntent
{
    HowToUseFeature = 0,
    BillingQuestion = 1,
    SubscriptionQuestion = 2,
    CreditBalanceQuestion = 3,
    CreditPolicyQuestion = 4,
    WhatsAppTemplateIssue = 5,
    WhatsAppConnectionIssue = 6,
    WhatsAppPolicyQuestion = 7,
    CampaignIssue = 8,
    LeadDiscoveryQuestion = 9,
    TechnicalError = 10,
    RefundRequest = 11,
    CancellationRequest = 12,
    SecurityPrivacyConcern = 13,
    LegalComplianceQuery = 14,
    AccountChangeRequest = 15,
    Complaint = 16,
    HumanAgentRequest = 17,
    FeatureRequest = 18,

    /// <summary>The classifier could not tell. Treated as Prohibited: not knowing what was asked is
    /// exactly when an autonomous answer is least safe.</summary>
    Unknown = 19
}

/// <summary>How much an autonomous answer to an intent is allowed to matter. Prohibited intents are
/// never answered by the agent at all - they go to a human whatever the retrieval found.</summary>
public enum SupportRiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Prohibited = 3
}

public static class SupportIntentRules
{
    public static SupportRiskLevel RiskOf(SupportIntent intent) => intent switch
    {
        SupportIntent.HowToUseFeature or SupportIntent.CreditBalanceQuestion or SupportIntent.CreditPolicyQuestion
            or SupportIntent.WhatsAppTemplateIssue or SupportIntent.WhatsAppPolicyQuestion
            or SupportIntent.LeadDiscoveryQuestion or SupportIntent.FeatureRequest => SupportRiskLevel.Low,

        SupportIntent.BillingQuestion or SupportIntent.SubscriptionQuestion
            or SupportIntent.WhatsAppConnectionIssue or SupportIntent.CampaignIssue
            or SupportIntent.TechnicalError => SupportRiskLevel.Medium,

        SupportIntent.RefundRequest or SupportIntent.CancellationRequest
            or SupportIntent.AccountChangeRequest or SupportIntent.Complaint => SupportRiskLevel.High,

        // SecurityPrivacyConcern, LegalComplianceQuery, HumanAgentRequest, Unknown.
        _ => SupportRiskLevel.Prohibited
    };

    /// <summary>The module an intent implies when the ticket does not name one; null when the intent
    /// says nothing about module (the module is then detected from the text, or left unset).</summary>
    public static ProductModule? DefaultModule(SupportIntent intent) => intent switch
    {
        SupportIntent.BillingQuestion or SupportIntent.SubscriptionQuestion
            or SupportIntent.RefundRequest or SupportIntent.CancellationRequest => ProductModule.Billing,
        SupportIntent.CreditBalanceQuestion or SupportIntent.CreditPolicyQuestion => ProductModule.Quota,
        SupportIntent.WhatsAppTemplateIssue => ProductModule.MessageTemplates,
        SupportIntent.WhatsAppConnectionIssue or SupportIntent.WhatsAppPolicyQuestion => ProductModule.WhatsApp,
        SupportIntent.CampaignIssue => ProductModule.Campaigns,
        SupportIntent.LeadDiscoveryQuestion => ProductModule.LeadDiscovery,
        SupportIntent.AccountChangeRequest => ProductModule.Users,
        SupportIntent.SecurityPrivacyConcern => ProductModule.Settings,
        SupportIntent.LegalComplianceQuery => ProductModule.Platform,
        _ => null
    };
}
