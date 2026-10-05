using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Reports;
using WhatsAppSalesAutomation.Domain.Entities.SocialAds;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.SocialAds;

public class MarketingComparisonService : IMarketingComparisonService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly IWhatsAppSpendService _whatsAppSpend;
    private readonly IDateTimeProvider _clock;

    public MarketingComparisonService(
        IApplicationDbContext context, ITenantContext tenant, IWhatsAppSpendService whatsAppSpend, IDateTimeProvider clock)
    {
        _context = context;
        _tenant = tenant;
        _whatsAppSpend = whatsAppSpend;
        _clock = clock;
    }

    public async Task<MarketingComparisonDto> GetAsync(int months, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenant.TenantId ?? throw new InvalidOperationException("Marketing comparison request has no tenant in scope.");
        var period = RevenuePeriod.Resolve(months, _clock.UtcNow);

        var country = await _context.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId).Select(t => t.CountryCode).FirstOrDefaultAsync(cancellationToken);
        var pricing = RegionalPricingCatalog.Resolve(country);

        var status = await _context.SocialAdConnections.Select(c => (SocialAdConnectionStatus?)c.Status).FirstOrDefaultAsync(cancellationToken);
        var connectionStatus = status?.ToString() ?? "NotConnected";

        // Revenue and sales: what the tenant recorded as sold in the period.
        var sales = await _context.PackageSales
            .Where(s => s.SoldAt >= period.From && s.SoldAt <= period.To)
            .Select(s => s.Amount)
            .ToListAsync(cancellationToken);
        var revenue = sales.Sum();

        var spendRows = await _context.SocialAdSpends
            .Where(s => s.Date >= period.From.Date && s.Date <= period.To.Date)
            .ToListAsync(cancellationToken);

        // Real ad data wins over typed-in months when both exist; mixing them would count a month twice.
        var metaRows = spendRows.Where(r => r.Source == SocialAdSpend.SourceMeta).ToList();
        var rows = metaRows.Count > 0 ? metaRows : spendRows;
        var source = metaRows.Count > 0 ? SocialAdSpend.SourceMeta : rows.Count > 0 ? SocialAdSpend.SourceManual : "None";

        var spendCurrency = rows.Select(r => r.CurrencyCode).FirstOrDefault(c => !string.IsNullOrEmpty(c));
        var currencyMatches = spendCurrency is null || string.Equals(spendCurrency, pricing.CurrencyCode, StringComparison.OrdinalIgnoreCase);

        var spend = rows.Sum(r => r.Spend);
        var clicks = rows.Sum(r => r.Clicks);
        var leads = rows.Sum(r => r.Leads);

        var social = new SocialChannelDto(
            spend,
            rows.Sum(r => r.Impressions),
            clicks,
            leads,
            // Clicks and leads are counts, so these two do not depend on currencies matching.
            clicks == 0 ? null : Round(spend / clicks),
            leads == 0 ? null : Round(spend / leads),
            currencyMatches && sales.Count > 0 && spend > 0 ? Round(spend / sales.Count) : null,
            currencyMatches && spend > 0 ? Math.Round((double)(revenue / spend), 2) : null);

        var whatsApp = await GetWhatsAppAsync(tenantId, period, pricing, revenue, sales.Count, cancellationToken);

        string? cheaper = null;
        decimal? savings = null;
        if (social.CostPerSale is { } socialPerSale && whatsApp.CostPerSale is { } whatsAppPerSale && socialPerSale != whatsAppPerSale)
        {
            cheaper = whatsAppPerSale < socialPerSale ? "WhatsApp" : "Social";
            savings = Math.Abs(socialPerSale - whatsAppPerSale);
        }

        var platforms = rows
            .GroupBy(r => r.Platform)
            .Select(g => new SocialPlatformDto(
                g.Key,
                g.Sum(r => r.Spend),
                spend == 0 ? null : Math.Round((double)(g.Sum(r => r.Spend) / spend) * 100, 1),
                g.Sum(r => r.Clicks),
                g.Sum(r => r.Leads)))
            .OrderByDescending(p => p.Spend)
            .ToList();

        var spendByBucket = rows.GroupBy(r => period.BucketOf(r.Date)).ToDictionary(g => g.Key, g => g.Sum(r => r.Spend));
        var trend = period.BucketStarts().Select(start => new SpendTrendPointDto(start, spendByBucket.GetValueOrDefault(start))).ToList();

        return new MarketingComparisonDto(
            period.Months,
            source,
            connectionStatus,
            spendCurrency,
            pricing.CurrencyCode,
            currencyMatches,
            revenue,
            sales.Count,
            social,
            whatsApp,
            cheaper,
            savings,
            platforms,
            trend);
    }

    private async Task<WhatsAppChannelDto> GetWhatsAppAsync(
        Guid tenantId, RevenuePeriod period, RegionalPricing pricing, decimal revenue, int salesCount, CancellationToken cancellationToken)
    {
        var whatsApp = await _whatsAppSpend.GetForTenantAsync(tenantId, period.From, cancellationToken: cancellationToken);

        // Lead discovery is part of what the platform costs the tenant, so it is counted with WhatsApp sending.
        var runs = _context.LeadDiscoveryRuns.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.RanAtUtc >= period.From && r.RanAtUtc <= period.To);
        var discoveryUsd = await runs.CountAsync(cancellationToken) == 0 ? 0m : await runs.SumAsync(r => r.EstimatedCostUsd, cancellationToken);

        var cost = Round((whatsApp.EstimatedCostUsd + discoveryUsd) * pricing.RateToUsd);

        return new WhatsAppChannelDto(
            cost,
            whatsApp.MessagesSent,
            salesCount > 0 ? Round(cost / salesCount) : null,
            cost > 0 ? Math.Round((double)(revenue / cost), 2) : null);
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
