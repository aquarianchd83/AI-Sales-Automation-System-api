namespace WhatsAppSalesAutomation.Domain.Enums;

public enum TenantNotificationKind
{
    QuotaLow20 = 0,
    QuotaLow5 = 1,
    QuotaExhausted = 2,
    CreditsExpiring14 = 3,
    CreditsExpiring3 = 4,
    RefundApproved = 5,
    RefundRejected = 6,
    RefundExpired = 7,
    // The plan's billing period ends in a week / tomorrow: it renews (and is charged), or cannot renew.
    PlanExpiring7 = 8,
    PlanExpiring1 = 9,
    // Raised by TenantJobRunner for every run of a self-service job (TenantJobCatalog.SelfServiceKeys) -
    // one pair per run, not per Hangfire tick, so these track "your lead discovery/campaign job just ran"
    // rather than the job's own schedule.
    JobStarted = 10,
    JobCompleted = 11,
    // A platform operator granted the tenant extra units of a quota (QuotaType is set) - a one-off good-news
    // notice, so each grant has its own episode.
    CreditsAdded = 12,
    // Meta reviewed one of the tenant's message templates: Approved means campaigns can use it (and says if Meta
    // reclassified its category, which changes what a send costs); NeedsAttention means it was rejected, paused or
    // disabled, so it can no longer be sent. Stored as text (max 30 chars), so a new kind needs no migration.
    TemplateApproved = 13,
    TemplateNeedsAttention = 14,
    // The plan's billing period ended but it could not be renewed (no price for the tenant's country), so no new
    // quota was granted - the tenant should hear about it before they run dry, not discover it.
    PlanRenewalFailed = 15,
    // The WhatsApp access token could not be renewed, or will lapse with nothing able to renew it. When it does,
    // every send stops - and only the tenant can paste a new token.
    WhatsAppTokenFailing = 16
}
