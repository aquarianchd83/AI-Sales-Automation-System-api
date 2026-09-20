namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Shared string constants for <see cref="IPlatformAuditService.LogAsync"/>'s <c>action</c>
/// parameter - kept as constants (rather than free-typed at each call site) purely so every writer and
/// the Audit Log screen's own filter agree on exact spelling. Deliberately still a plain string on the
/// entity, not an enum - see <c>PlatformAuditLogEntry.Action</c>'s own doc comment for why.</summary>
public static class PlatformAuditActions
{
    public const string TenantCreated = "TenantCreated";
    public const string TenantSuspended = "TenantSuspended";
    public const string TenantReactivated = "TenantReactivated";
    public const string TenantDeleted = "TenantDeleted";
    public const string TenantImpersonated = "TenantImpersonated";
    public const string TenantPlanOverridden = "TenantPlanOverridden";
    public const string TenantTimezoneUpdated = "TenantTimezoneUpdated";
    public const string TenantCountryUpdated = "TenantCountryUpdated";
    public const string TenantWhatsAppConfigSaved = "TenantWhatsAppConfigSaved";
    public const string TenantWhatsAppConfigDeleted = "TenantWhatsAppConfigDeleted";
    public const string TenantAiConfigSaved = "TenantAiConfigSaved";
    public const string TenantAiConfigDeleted = "TenantAiConfigDeleted";
    public const string TenantConfigOverridesSaved = "TenantConfigOverridesSaved";
    public const string TenantConfigOverridesDeleted = "TenantConfigOverridesDeleted";
    public const string PlanCreated = "PlanCreated";
    public const string PlanUpdated = "PlanUpdated";
    public const string PlanDeactivated = "PlanDeactivated";
    public const string AnnouncementCreated = "AnnouncementCreated";
    public const string AnnouncementUpdated = "AnnouncementUpdated";
    public const string AnnouncementDeleted = "AnnouncementDeleted";
    public const string TenantJobScheduleUpdated = "TenantJobScheduleUpdated";
    public const string TenantJobTriggered = "TenantJobTriggered";
    public const string TenantJobsReconciled = "TenantJobsReconciled";
    public const string TenantQuotaAdjusted = "TenantQuotaAdjusted";
    public const string RefundApproved = "RefundApproved";
    public const string RefundRejected = "RefundRejected";
    public const string RefundIssued = "RefundIssued";
    public const string RefundRequestsToggled = "RefundRequestsToggled";
    public const string CreditPackCreated = "CreditPackCreated";
    public const string CreditPackUpdated = "CreditPackUpdated";
    public const string CreditPackDeactivated = "CreditPackDeactivated";
    public const string ConfigurationUpdated = "ConfigurationUpdated";
    public const string FlowchartCreated = "FlowchartCreated";
    public const string FlowchartUpdated = "FlowchartUpdated";
    public const string FlowchartDeleted = "FlowchartDeleted";
    public const string FaqEntryCreated = "FaqEntryCreated";
    public const string FaqEntryUpdated = "FaqEntryUpdated";
    public const string FaqEntryDeleted = "FaqEntryDeleted";
}
