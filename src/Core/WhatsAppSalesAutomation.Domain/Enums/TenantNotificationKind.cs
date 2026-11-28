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
    WhatsAppTokenFailing = 16,
    // Someone's account was locked after repeated wrong passwords. Tells the tenant's admins while it is happening - it is
    // either a forgotten password or somebody guessing, and only a person can tell which.
    AccountLocked = 17,
    // Meta rated the tenant's WhatsApp number down (Yellow): the daily cap is halved until it recovers. Tells them
    // while there is still time to change what they send.
    QualityWarning = 18,
    // Sending stopped: the number's quality fell to Red and the account is held for review.
    SendingPaused = 19,
    // Sent to the product owner (never a tenant) when a platform setting such as a Simulated AI provider needs fixing.
    PlatformSetupNeeded = 20,
    // Discovery was not started because the WhatsApp Consent Page is incomplete, unpublished or switched off. Nothing was charged.
    // The tenant fixes it in WhatsApp Consent Page Settings.
    ConsentPageSetupRequired = 21,
    // A customer agreed a demo, visit, appointment or similar with the AI and gave what it needs (when, topics...). Tells the tenant, so a person is
    // ready for it: the AI has no calendar, so the booking is only as real as the tenant's follow-through.
    NextStepBooked = 22,
    // The platform admin attached the video a tenant asked for in the Story studio. In-app only: the tenant opens Story studio to watch it.
    StoryVideoReady = 23,
    // The platform admin declined a video request (it could not be made): the credits were returned, and the reason is in the notice.
    StoryVideoRejected = 24,
    // Razorpay took the month's automatic payment for the plan: the plan is paid for another period and its credits are in the wallet.
    AutoPayPaymentReceived = 25,
    // An automatic payment did not go through. Razorpay tries again by itself over the next few days; the tenant can make sure there is money to take.
    AutoPayPaymentFailed = 26,
    // Every retry failed and Razorpay has stopped trying. The plan ends unless the tenant sets up automatic payment again.
    AutoPayHalted = 27,
    // The mandate was cancelled - by the tenant, in their UPI app, or by their bank. The plan runs to the end of what was paid for.
    AutoPayCancelled = 28,
    // The tenant paused the mandate in their UPI app: nothing is taken until they resume it there.
    AutoPayPaused = 29,
    // The tenant's WhatsApp QR session dropped (phone unlinked, logged out, or the connection failed), so every send is paused until they scan a new QR.
    // Raised by the twice-daily session check, once per disconnect, through the platform's own WhatsApp number and email account.
    WhatsAppSessionDisconnected = 30,
    // Not a notice: the template a platform admin starts (or re-opens) a WhatsApp chat with a tenant on the Tenant conversations page, when the
    // 24-hour window is closed and free text would not be delivered. Never raised as a TenantNotification.
    AdminConversation = 31
}
