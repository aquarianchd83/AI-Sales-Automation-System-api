using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformDashboardService : IPlatformDashboardService
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserPricingService _pricing;
    private readonly IDateTimeProvider _dateTime;

    private readonly IAiSpendEstimator _aiSpend;

    public PlatformDashboardService(
        IApplicationDbContext context,
        ICurrentUserPricingService pricing,
        IDateTimeProvider dateTime,
        IAiSpendEstimator aiSpend)
    {
        _aiSpend = aiSpend;
        _context = context;
        _pricing = pricing;
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

        var estimatedAiSpend = aiInteractions.Sum(a => _aiSpend.EstimateUsd(a.ModelUsed, a.PromptTokens, a.CompletionTokens));

        var webhookFailuresLast24h = await _context.WebhookEvents.IgnoreQueryFilters()
            .CountAsync(w => w.ReceivedAt >= last24h && w.ProcessingStatus == WebhookProcessingStatus.Failed, cancellationToken);

        // Platform-wide numbers, so they are quoted in the operator's own currency - not any one tenant's.
        var pricing = await _pricing.GetAsync(cancellationToken);
        var mrrUsd = mrrCents / 100m;

        return new PlatformDashboardDto(
            tenantCountsByStatus.GetValueOrDefault(TenantStatus.Active),
            tenantCountsByStatus.GetValueOrDefault(TenantStatus.Trial),
            tenantCountsByStatus.GetValueOrDefault(TenantStatus.Suspended),
            signupsThisMonth,
            mrrUsd,
            messagesSentThisMonth,
            aiInteractions.Count,
            estimatedAiSpend,
            webhookFailuresLast24h,
            // Arbitrary but documented threshold - a real "system health" signal (queue depth,
            // provider outage detection, etc.) is out of scope here; this is a rough "is something
            // clearly on fire" flag for the dashboard tile, not a monitoring system.
            webhookFailuresLast24h < 20,
            pricing.CurrencyCode,
            pricing.CurrencySymbol,
            ToLocal(mrrUsd, pricing),
            ToLocal(estimatedAiSpend, pricing));
    }

    /// <summary>Six decimal places, the same precision the other estimate-to-local conversions keep (see
    /// TenantChargesService) - a cheap month costs a fraction of a cent, and rounding here would hide it.
    /// </summary>
    private static decimal ToLocal(decimal usd, RegionalPricing pricing) =>
        Math.Round(usd * pricing.RateToUsd, 6, MidpointRounding.AwayFromZero);
}
