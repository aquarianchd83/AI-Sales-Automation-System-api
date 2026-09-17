using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformUsageService : IPlatformUsageService
{
    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWhatsAppSpendService _whatsAppSpend;
    private readonly IDateTimeProvider _dateTime;

    public PlatformUsageService(
        IApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IWhatsAppSpendService whatsAppSpend,
        IDateTimeProvider dateTime)
    {
        _context = context;
        _userManager = userManager;
        _whatsAppSpend = whatsAppSpend;
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

        // Spend is measured over each tenant's OWN calendar month (TenantMonth), so a figure here matches
        // what that tenant sees on its own Settings page rather than being cut at UTC midnight. The message
        // and user quota columns above deliberately keep the UTC month PlanLimitsService actually enforces.
        var timezones = await _context.Tenants.IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Timezone })
            .ToListAsync(cancellationToken);

        var spendStartByTenant = timezones.ToDictionary(t => t.Id, t => TenantMonth.StartUtc(t.Timezone, now));

        var aiByTenant = new Dictionary<Guid, (int Count, decimal Spend)>();
        var whatsAppByTenant = new Dictionary<Guid, WhatsAppSpend>();
        var discoveryByTenant = new Dictionary<Guid, (int Runs, int Leads, decimal CostUsd)>();

        // Tenants sharing a timezone share a window, so this is one pass per distinct month start - one
        // iteration for the common case where every tenant is in the same zone, never one query per tenant.
        foreach (var window in spendStartByTenant.GroupBy(entry => entry.Value))
        {
            var spendStartUtc = window.Key;
            var windowTenantIds = window.Select(entry => entry.Key).ToList();

            var aiInteractions = await _context.AiInteractions.IgnoreQueryFilters()
                .Where(a => windowTenantIds.Contains(a.TenantId) && a.CreatedAt >= spendStartUtc)
                .Select(a => new { a.TenantId, a.ModelUsed, a.PromptTokens, a.CompletionTokens })
                .ToListAsync(cancellationToken);

            foreach (var tenantAi in aiInteractions.GroupBy(a => a.TenantId))
            {
                aiByTenant[tenantAi.Key] = (
                    tenantAi.Count(),
                    tenantAi.Sum(a => AiSpendEstimator.EstimateUsd(a.ModelUsed, a.PromptTokens, a.CompletionTokens)));
            }

            // Priced from the messages themselves, so this row and the tenant's own Settings page agree.
            foreach (var entry in await _whatsAppSpend.GetForTenantsAsync(windowTenantIds, spendStartUtc, cancellationToken))
                whatsAppByTenant[entry.Key] = entry.Value;

            var discoveryRows = await _context.LeadDiscoveryRuns.IgnoreQueryFilters()
                .Where(r => windowTenantIds.Contains(r.TenantId) && r.RanAtUtc >= spendStartUtc)
                .GroupBy(r => r.TenantId)
                .Select(g => new
                {
                    TenantId = g.Key,
                    Runs = g.Count(),
                    Leads = g.Sum(r => r.LeadsSaved),
                    CostUsd = g.Sum(r => r.EstimatedCostUsd)
                })
                .ToListAsync(cancellationToken);

            foreach (var row in discoveryRows)
                discoveryByTenant[row.TenantId] = (row.Runs, row.Leads, row.CostUsd);
        }

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
            var whatsApp = whatsAppByTenant.GetValueOrDefault(t.Id, WhatsAppSpend.Empty);
            var (discoveryRuns, discoveryLeads, discoverySpend) = discoveryByTenant.GetValueOrDefault(t.Id);

            return new PlatformTenantUsageDto(
                t.Id, t.Name,
                messagesSent, plan?.MaxMessagesPerMonth, plan is not null && messagesSent >= plan.MaxMessagesPerMonth,
                userCount, plan?.MaxUsers, plan is not null && userCount >= plan.MaxUsers,
                aiCount, aiSpend,
                whatsApp.BillableMessages, whatsApp.EstimatedCostUsd,
                discoveryRuns, discoveryLeads, discoverySpend,
                Math.Round(aiSpend + whatsApp.EstimatedCostUsd + discoverySpend, 6, MidpointRounding.AwayFromZero),
                spendStartByTenant.GetValueOrDefault(t.Id, monthStartUtc));
        }).ToList();

        return new PagedResult<PlatformTenantUsageDto>(items, totalCount, query.Page, query.PageSize);
    }
}
