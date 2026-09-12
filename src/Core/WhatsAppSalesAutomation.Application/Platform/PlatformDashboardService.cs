using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformDashboardService : IPlatformDashboardService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;

    public PlatformDashboardService(IApplicationDbContext context, IDateTimeProvider dateTime)
    {
        _context = context;
        _dateTime = dateTime;
    }

    public async Task<PlatformDashboardDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var monthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last24h = now.AddHours(-24);

        var tenantCountsByStatus = await _context.Tenants
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken);

        var signupsThisMonth = await _context.Tenants.CountAsync(t => t.CreatedAt >= monthStartUtc, cancellationToken);

        // MRR: sum of PriceMonthlyCents for every tenant currently Trialing or Active on a plan - see
        // this DTO's own doc comment for why this isn't a true prorated figure.
        var mrrCents = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.PlanId != null && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .Join(_context.Plans, s => s.PlanId, p => p.Id, (s, p) => p.PriceMonthlyCents)
            .SumAsync(cancellationToken);

        var messagesSentThisMonth = await _context.Messages.IgnoreQueryFilters()
            .CountAsync(m => m.CreatedAt >= monthStartUtc, cancellationToken);

        var aiInteractions = await _context.AiInteractions.IgnoreQueryFilters()
            .Where(a => a.CreatedAt >= monthStartUtc)
            .Select(a => new { a.ModelUsed, a.PromptTokens, a.CompletionTokens })
            .ToListAsync(cancellationToken);

        var estimatedAiSpend = aiInteractions.Sum(a => AiSpendEstimator.EstimateUsd(a.ModelUsed, a.PromptTokens, a.CompletionTokens));

        var webhookFailuresLast24h = await _context.WebhookEvents.IgnoreQueryFilters()
            .CountAsync(w => w.ReceivedAt >= last24h && w.ProcessingStatus == WebhookProcessingStatus.Failed, cancellationToken);

        return new PlatformDashboardDto(
            tenantCountsByStatus.GetValueOrDefault(TenantStatus.Active),
            tenantCountsByStatus.GetValueOrDefault(TenantStatus.Trial),
            tenantCountsByStatus.GetValueOrDefault(TenantStatus.Suspended),
            signupsThisMonth,
            mrrCents / 100m,
            messagesSentThisMonth,
            aiInteractions.Count,
            estimatedAiSpend,
            webhookFailuresLast24h,
            // Arbitrary but documented threshold - a real "system health" signal (queue depth,
            // provider outage detection, etc.) is out of scope here; this is a rough "is something
            // clearly on fire" flag for the dashboard tile, not a monitoring system.
            webhookFailuresLast24h < 20);
    }
}
