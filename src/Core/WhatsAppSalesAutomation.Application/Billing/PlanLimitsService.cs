using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>See <see cref="IPlanLimitsService"/>'s own doc comment for the overall reasoning. Lives
/// in Application, not Infrastructure, despite the name pattern of its Stripe-adjacent siblings -
/// every check here is plain business logic against IApplicationDbContext/UserManager, with no
/// external system involved, the same "Application, not Infrastructure" placement as CampaignService.
///
/// Every count query explicitly filters by the passed <c>tenantId</c> with <c>IgnoreQueryFilters()</c>
/// rather than trusting the ambient ITenantContext the ordinary tenant-scoped query filter would use -
/// this makes the guard's result depend only on its own parameter, not on whatever happens to be
/// ambient when it's called, which matters because every call site here already resolved tenantId
/// from ambient context itself; a guard that silently re-derived it a second, different way would be
/// strictly worse, not better.
/// </summary>
public class PlanLimitsService : IPlanLimitsService
{
    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDateTimeProvider _dateTime;

    public PlanLimitsService(IApplicationDbContext context, UserManager<ApplicationUser> userManager, IDateTimeProvider dateTime)
    {
        _context = context;
        _userManager = userManager;
        _dateTime = dateTime;
    }

    public async Task EnsureCanAddUserAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(tenantId, cancellationToken);
        if (plan is null)
            return;

        var currentCount = await _userManager.Users.IgnoreQueryFilters()
            .CountAsync(u => u.TenantId == tenantId, cancellationToken);

        if (currentCount >= plan.MaxUsers)
            throw new PlanLimitExceededException($"Your plan allows up to {plan.MaxUsers} users. Upgrade to add more.");
    }

    public async Task EnsureCanSendMessageAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(tenantId, cancellationToken);
        if (plan is null)
            return;

        var sentThisMonth = await CountMessagesSentThisMonthAsync(tenantId, cancellationToken);

        if (sentThisMonth >= plan.MaxMessagesPerMonth)
            throw new PlanLimitExceededException($"Your plan allows up to {plan.MaxMessagesPerMonth} messages per month. Upgrade to send more.");
    }

    public async Task<TenantMessageUsageDto> GetMessageUsageAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(tenantId, cancellationToken);
        var sentThisMonth = await CountMessagesSentThisMonthAsync(tenantId, cancellationToken);

        return new TenantMessageUsageDto(sentThisMonth, plan?.MaxMessagesPerMonth);
    }

    public async Task EnsureCanCreateCampaignAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(tenantId, cancellationToken);
        if (plan is null)
            return;

        var currentCount = await _context.Campaigns.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == tenantId, cancellationToken);

        if (currentCount >= plan.MaxCampaigns)
            throw new PlanLimitExceededException($"Your plan allows up to {plan.MaxCampaigns} campaigns. Upgrade to create more.");
    }

    public async Task EnsureCanCreateKnowledgeBaseArticleAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(tenantId, cancellationToken);
        if (plan is null)
            return;

        // IgnoreQueryFilters() bypasses both halves of KnowledgeBaseArticle's combined filter (tenant
        // + soft-delete - see ApplicationDbContext.SetTenantOwnedFilter), so IsDeleted is re-applied
        // by hand here the same way InboundWebhookProcessor's Customer lookup does.
        var currentCount = await _context.KnowledgeBaseArticles.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tenantId && !a.IsDeleted, cancellationToken);

        if (currentCount >= plan.MaxKnowledgeBaseArticles)
            throw new PlanLimitExceededException($"Your plan allows up to {plan.MaxKnowledgeBaseArticles} knowledge base articles. Upgrade to add more.");
    }

    private async Task<int> CountMessagesSentThisMonthAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = _dateTime.UtcNow;
        var monthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        return await _context.Messages.IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.CreatedAt >= monthStartUtc, cancellationToken);
    }

    /// <summary>Null means "no limits apply" - a tenant with no Subscription row (never completed
    /// Checkout; still on AuthService.SignUpAsync's 14-day trial) or one whose Subscription has no
    /// PlanId yet. See IPlanLimitsService's own doc comment for why that's the right default rather
    /// than the harshest one.</summary>
    private async Task<Plan?> GetPlanAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        if (subscription?.PlanId is not { } planId)
            return null;

        return await _context.Plans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
    }
}
