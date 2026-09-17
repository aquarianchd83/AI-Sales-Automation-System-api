namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// Guards a tenant-owned resource against its plan's limits - each Ensure* method throws
/// <c>PlanLimitExceededException</c> (mapped to HTTP 402) if the tenant is already at capacity,
/// otherwise returns normally, so a call site simply awaits the guard before its own mutation with no
/// branching. Every check is a live count against <c>IApplicationDbContext</c> - no caching or
/// denormalized counters for v1, since the extra correctness that would buy (avoiding a handful of
/// COUNT queries per creation/send) isn't worth the staleness risk this early.
///
/// A tenant with no <c>Subscription</c> row yet (brand-new, still on <c>AuthService.SignUpAsync</c>'s
/// 14-day trial) is treated as unlimited by every guard here - trial enforcement is
/// AuthService.LoginAsync's <c>Tenant.TrialEndsAtUtc</c> check, a separate concern from plan limits,
/// which only start applying once a tenant actually has a paid plan.
/// </summary>
public interface IPlanLimitsService
{
    Task EnsureCanAddUserAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Counts Messages created since the start of the current UTC calendar month - a simple,
    /// honest "this month" definition rather than a rolling 30-day window, matching how a Stripe
    /// billing period is communicated to a tenant ("resets on the 1st"), even though it doesn't
    /// exactly track CurrentPeriodEndUtc's own cycle.</summary>
    Task EnsureCanSendMessageAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task EnsureCanCreateCampaignAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task EnsureCanCreateKnowledgeBaseArticleAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The most new leads one lead discovery run may add for this tenant
    /// (Plan.MaxLeadDiscoveryBatchSize). Null when no plan applies - the same "no Subscription/PlanId yet
    /// -> unlimited" treatment as the guards above, which leaves the profile's own validated BatchSize as
    /// the limit.</summary>
    Task<int?> GetLeadDiscoveryBatchLimitAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Read-only counterpart to <see cref="EnsureCanSendMessageAsync"/> - same "since the
    /// start of the current UTC calendar month" count, same "no Subscription/PlanId yet -> unlimited"
    /// treatment (returned as a null <see cref="TenantMessageUsageDto.MaxMessagesPerMonth"/> rather
    /// than throwing), but never throws. Backs the tenant Settings page's WhatsApp usage display.</summary>
    Task<TenantMessageUsageDto> GetMessageUsageAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
