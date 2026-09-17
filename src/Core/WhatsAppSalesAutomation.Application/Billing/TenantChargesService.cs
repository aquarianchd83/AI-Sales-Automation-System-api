using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Tenancy;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>See <see cref="ITenantChargesService"/>. Composes the two per-use costs this platform has -
/// WhatsApp sending (priced on read by <see cref="IWhatsAppSpendService"/>) and lead discovery (priced when
/// each run happened and stored on the run) - into one figure for the tenant's Settings page.</summary>
public class TenantChargesService : ITenantChargesService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IWhatsAppSpendService _whatsAppSpend;
    private readonly IDateTimeProvider _dateTime;

    public TenantChargesService(
        IApplicationDbContext context,
        ITenantContext tenantContext,
        IWhatsAppSpendService whatsAppSpend,
        IDateTimeProvider dateTime)
    {
        _context = context;
        _tenantContext = tenantContext;
        _whatsAppSpend = whatsAppSpend;
        _dateTime = dateTime;
    }

    public async Task<TenantChargesDto> GetCurrentMonthAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Authenticated tenant-charges request has no tenant in scope.");

        var tenant = await _context.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.CountryCode, t.Timezone })
            .FirstOrDefaultAsync(cancellationToken);

        // The tenant's own calendar month, not the platform's: a tenant in Asia/Kolkata starts its month
        // 5.5 hours before UTC does, and this figure is about that tenant's spending. The message allowance
        // shown beside it still runs on the UTC month PlanLimitsService enforces - see TenantMonth.
        var monthStartUtc = TenantMonth.StartUtc(tenant?.Timezone, _dateTime.UtcNow);
        var pricing = RegionalPricingCatalog.Resolve(tenant?.CountryCode);

        var whatsApp = await _whatsAppSpend.GetForTenantAsync(tenantId, monthStartUtc, cancellationToken);

        // Aggregated with scalar queries rather than one grouped projection - see this codebase's EF Core
        // note about operators chained after a projection.
        var runs = _context.LeadDiscoveryRuns.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.RanAtUtc >= monthStartUtc);
        var runCount = await runs.CountAsync(cancellationToken);
        var leadsSaved = runCount == 0 ? 0 : await runs.SumAsync(r => r.LeadsSaved, cancellationToken);
        var discoveryUsd = runCount == 0 ? 0m : await runs.SumAsync(r => r.EstimatedCostUsd, cancellationToken);

        var totalUsd = Round(whatsApp.EstimatedCostUsd + discoveryUsd);

        return new TenantChargesDto(
            pricing.CurrencyCode,
            pricing.CurrencySymbol,
            monthStartUtc,
            new TenantWhatsAppChargesDto(
                whatsApp.MessagesSent,
                whatsApp.BillableMessages,
                whatsApp.FreeMessages,
                whatsApp.EstimatedCostUsd,
                ToLocal(whatsApp.EstimatedCostUsd, pricing),
                whatsApp.ByCategory
                    .Select(c => new TenantWhatsAppCategoryChargeDto(
                        c.Category, c.Messages, c.RatePerMessageUsd, c.EstimatedCostUsd, ToLocal(c.EstimatedCostUsd, pricing)))
                    .ToList()),
            new TenantLeadDiscoveryChargesDto(runCount, leadsSaved, discoveryUsd, ToLocal(discoveryUsd, pricing)),
            totalUsd,
            ToLocal(totalUsd, pricing));
    }

    /// <summary>The tenant's own currency, from its Country - falls back to USD like every other price in
    /// this application (see RegionalPricingCatalog.Resolve).</summary>
    private static decimal ToLocal(decimal usd, RegionalPricing pricing) => Round(usd * pricing.RateToUsd);

    private static decimal Round(decimal amount) => Math.Round(amount, 6, MidpointRounding.AwayFromZero);
}
