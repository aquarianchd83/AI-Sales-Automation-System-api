using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformBillingService : IPlatformBillingService
{
    private readonly IApplicationDbContext _context;

    public PlatformBillingService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<PlatformPlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Plans
            .OrderBy(p => p.PriceMonthlyCents)
            .Select(p => new PlatformPlanDto(
                p.Id, p.Code, p.Name, p.StripePriceId, p.MaxUsers, p.MaxMessagesPerMonth,
                p.MaxCampaigns, p.MaxKnowledgeBaseArticles, p.PriceMonthlyCents, p.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<PlatformSubscriptionListItemDto>> GetSubscriptionsAsync(PlatformSubscriptionQuery query, CancellationToken cancellationToken = default)
    {
        var subscriptions = _context.Subscriptions.IgnoreQueryFilters().AsQueryable();

        if (query.Status is { } status)
            subscriptions = subscriptions.Where(s => s.Status == status);

        var joined = subscriptions
            .Join(_context.Tenants, s => s.TenantId, t => t.Id, (s, t) => new { s, t })
            .GroupJoin(_context.Plans, x => x.s.PlanId, p => (Guid?)p.Id, (x, plans) => new { x.s, x.t, plans })
            .SelectMany(x => x.plans.DefaultIfEmpty(), (x, p) => new { x.s, x.t, p });

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            joined = joined.Where(x => x.t.Name.Contains(search) || x.t.Slug.Contains(search));
        }

        var totalCount = await joined.CountAsync(cancellationToken);

        var items = await joined
            .OrderByDescending(x => x.s.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new PlatformSubscriptionListItemDto(
                x.t.Id, x.t.Name, x.p == null ? null : x.p.Name, x.s.Status, x.s.CurrentPeriodEndUtc, x.s.Status == SubscriptionStatus.PastDue))
            .ToListAsync(cancellationToken);

        return new PagedResult<PlatformSubscriptionListItemDto>(items, totalCount, query.Page, query.PageSize);
    }
}
