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
    private readonly Common.Options.FxOptions _fx;

    public PlatformDashboardService(
        IApplicationDbContext context,
        ICurrentUserPricingService pricing,
        IDateTimeProvider dateTime,
        IAiSpendEstimator aiSpend,
        Microsoft.Extensions.Options.IOptionsSnapshot<Common.Options.FxOptions> fx)
    {
        _fx = fx.Value;
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
        // Each tenant's monthly price is the one set for ITS country, in that currency; every one is turned into rupees at the
        // platform's own rate so they can be added. A tenant on a plan with no price in its country adds nothing.
        var live = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.PlanId != null && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing))
            .Join(_context.Tenants.IgnoreQueryFilters(), s => s.TenantId, t => t.Id, (s, t) => new { PlanId = s.PlanId!.Value, t.CountryCode })
            .ToListAsync(cancellationToken);
        var planPrices = await Billing.CatalogPricing.LoadPlanPricesAsync(_context, live.Select(l => l.PlanId).Distinct().ToList(), cancellationToken);
        var mrrInr = 0m;
        foreach (var l in live)
        {
            var region = RegionalPricing_Resolve(l.CountryCode);
            var price = planPrices.GetValueOrDefault(l.PlanId)?.GetValueOrDefault(region.CountryCode) ?? 0m;
            mrrInr += price / region.RateToUsd * _fx.InrPerUsd;
        }

        var mrrCents = (int)Math.Round(mrrInr / _fx.InrPerUsd * 100m, MidpointRounding.AwayFromZero);

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
            Math.Round(mrrInr, 2, MidpointRounding.AwayFromZero),
            ToLocal(estimatedAiSpend, pricing));
    }

    private static Billing.RegionalPricing RegionalPricing_Resolve(string? code) => Billing.RegionalPricingCatalog.Resolve(code);

    /// <summary>Six decimal places, the same precision the other estimate-to-local conversions keep (see
    /// TenantChargesService) - a cheap month costs a fraction of a cent, and rounding here would hide it.
    /// </summary>
    private static decimal ToLocal(decimal usd, RegionalPricing pricing) =>
        Math.Round(usd * pricing.RateToUsd, 6, MidpointRounding.AwayFromZero);
}
