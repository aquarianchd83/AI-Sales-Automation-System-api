using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformUsageService : IPlatformUsageService
{
    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDateTimeProvider _dateTime;

    public PlatformUsageService(IApplicationDbContext context, UserManager<ApplicationUser> userManager, IDateTimeProvider dateTime)
    {
        _context = context;
        _userManager = userManager;
        _dateTime = dateTime;
    }

    public async Task<PagedResult<PlatformTenantUsageDto>> GetPagedAsync(PagedRequest query, CancellationToken cancellationToken = default)
    {
        var tenants = _context.Tenants.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            tenants = tenants.Where(t => t.Name.Contains(search) || t.Slug.Contains(search));
        }

        var totalCount = await tenants.CountAsync(cancellationToken);

        var page = await tenants
            .OrderByDescending(t => t.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);

        var tenantIds = page.Select(t => t.Id).ToList();

        var now = _dateTime.UtcNow;
        var monthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var messageCounts = await _context.Messages.IgnoreQueryFilters()
            .Where(m => tenantIds.Contains(m.TenantId) && m.CreatedAt >= monthStartUtc)
            .GroupBy(m => m.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

        var userCounts = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId != null && tenantIds.Contains(u.TenantId!.Value))
            .GroupBy(u => u.TenantId!.Value)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

        var aiInteractions = await _context.AiInteractions.IgnoreQueryFilters()
            .Where(a => tenantIds.Contains(a.TenantId) && a.CreatedAt >= monthStartUtc)
            .Select(a => new { a.TenantId, a.ModelUsed, a.PromptTokens, a.CompletionTokens })
            .ToListAsync(cancellationToken);

        var aiByTenant = aiInteractions.GroupBy(a => a.TenantId).ToDictionary(
            g => g.Key,
            g => (Count: g.Count(), Spend: g.Sum(a => AiSpendEstimator.EstimateUsd(a.ModelUsed, a.PromptTokens, a.CompletionTokens))));

        var subscriptions = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => tenantIds.Contains(s.TenantId) && s.PlanId != null)
            .Select(s => new { s.TenantId, s.PlanId })
            .ToListAsync(cancellationToken);

        var planIds = subscriptions.Select(s => s.PlanId!.Value).Distinct().ToList();
        var plans = await _context.Plans.Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var planByTenant = subscriptions.ToDictionary(s => s.TenantId, s => plans.GetValueOrDefault(s.PlanId!.Value));

        var items = page.Select(t =>
        {
            var plan = planByTenant.GetValueOrDefault(t.Id);
            var messagesSent = messageCounts.GetValueOrDefault(t.Id);
            var userCount = userCounts.GetValueOrDefault(t.Id);
            var (aiCount, aiSpend) = aiByTenant.GetValueOrDefault(t.Id);

            return new PlatformTenantUsageDto(
                t.Id, t.Name,
                messagesSent, plan?.MaxMessagesPerMonth, plan is not null && messagesSent >= plan.MaxMessagesPerMonth,
                userCount, plan?.MaxUsers, plan is not null && userCount >= plan.MaxUsers,
                aiCount, aiSpend);
        }).ToList();

        return new PagedResult<PlatformTenantUsageDto>(items, totalCount, query.Page, query.PageSize);
    }
}
