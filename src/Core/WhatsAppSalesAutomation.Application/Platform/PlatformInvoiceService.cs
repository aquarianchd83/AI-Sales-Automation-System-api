using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>See <see cref="IPlatformInvoiceService"/> and <see cref="Invoice"/>'s own doc comment for
/// what a row here means and what it approximates.</summary>
public class PlatformInvoiceService : IPlatformInvoiceService
{
    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWhatsAppSpendService _whatsAppSpend;
    private readonly IDateTimeProvider _dateTime;

    public PlatformInvoiceService(
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

    public async Task<PagedResult<PlatformInvoiceListItemDto>> GetPagedAsync(PlatformInvoiceQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureInvoicesUpToDateAsync(cancellationToken);

        var invoices = _context.Invoices.IgnoreQueryFilters()
            .Join(_context.Tenants, i => i.TenantId, t => t.Id, (i, t) => new { i, t });

        if (query.Status is { } status)
            invoices = invoices.Where(x => x.i.Status == status);

        if (query.TenantId is { } tenantId)
            invoices = invoices.Where(x => x.i.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            invoices = invoices.Where(x => x.t.Name.Contains(search) || x.t.Slug.Contains(search));
        }

        var totalCount = await invoices.CountAsync(cancellationToken);

        var items = await invoices
            .OrderByDescending(x => x.i.PeriodStartUtc).ThenBy(x => x.t.Name)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new PlatformInvoiceListItemDto(
                x.i.Id, x.i.TenantId, x.t.Name, x.i.PeriodStartUtc, x.i.PeriodEndUtc, x.i.PlanName,
                x.i.Status, x.i.PaidAtUtc, x.i.TotalAmountUsd, x.i.TotalAmountLocal, x.i.CurrencyCode, x.i.CurrencySymbol))
            .ToListAsync(cancellationToken);

        return new PagedResult<PlatformInvoiceListItemDto>(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<PlatformInvoiceDetailDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _context.Invoices.IgnoreQueryFilters()
            .Join(_context.Tenants, i => i.TenantId, t => t.Id, (i, t) => new { i, t })
            .FirstOrDefaultAsync(x => x.i.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Invoice), id);

        return ToDetailDto(row.i, row.t.Name);
    }

    public async Task<PlatformInvoiceDetailDto> MarkPaidAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var invoice = await _context.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Invoice), id);

        // An Upcoming invoice is still accruing - nothing final to collect yet, and marking it paid
        // anyway would just get silently reverted back to Upcoming the next time
        // EnsureInvoicesUpToDateAsync refreshes this still-current period.
        if (invoice.Status == InvoiceStatus.Upcoming)
            throw new ConflictException("This invoice covers the current, still-open period and can't be marked paid until it closes.");

        if (invoice.Status != InvoiceStatus.Paid)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAtUtc = _dateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }

        var tenantName = await _context.Tenants
            .Where(t => t.Id == invoice.TenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return ToDetailDto(invoice, tenantName);
    }

    /// <summary>
    /// Backfills any (tenant, calendar UTC month) pair that doesn't have an invoice yet - including the
    /// current, still-open month, so a tenant shows up here as soon as it has a plan, not only once its
    /// first month has closed - and refreshes the current month's row (always Upcoming) in place every
    /// time this runs, so it reads as "month to date" until the month closes. Run at the top of every
    /// list read rather than on a schedule - there is no background job for this yet, and the unique
    /// (TenantId, PeriodStartUtc) index plus this always checking for existing rows first keeps it safe
    /// to call repeatedly.
    ///
    /// A row that has finalized to Due or Paid is never touched again - see Invoice's own doc comment
    /// for why. Upcoming is the one status this recomputes every call, which includes the one-time
    /// transition when a month rolls over: the row that was Upcoming through the whole month is, on the
    /// next call after it closes, recomputed one final time and flipped to Due. That means the figures
    /// it finalizes at depend on how recently someone last opened this screen before the rollover - if
    /// nobody looked since well before month-end, the final Due amount is stale rather than a true
    /// end-of-month total. A monthly close-out job would fix that; out of scope here.
    ///
    /// A Canceled subscription stops generating once its CurrentPeriodEndUtc's month has passed, rather
    /// than continuing to bill an inactive tenant forever.
    /// </summary>
    private async Task EnsureInvoicesUpToDateAsync(CancellationToken cancellationToken)
    {
        var now = _dateTime.UtcNow;
        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var subscriptions = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.PlanId != null)
            // A plan assigned through the Platform Admin's "Override plan" used to leave the period
            // null (fixed at the source, but rows created before that still exist) - fall back to when
            // the subscription row itself was created rather than silently never invoicing them.
            .Select(s => new { s.TenantId, PlanId = s.PlanId!.Value, s.Status, CurrentPeriodStartUtc = s.CurrentPeriodStartUtc ?? s.CreatedAt, s.CurrentPeriodEndUtc })
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0)
            return;

        var tenantIds = subscriptions.Select(s => s.TenantId).Distinct().ToList();

        // Tracked entities, not a projection - the current month's row (if any) is mutated in place
        // below rather than replaced.
        var existingByKey = (await _context.Invoices.IgnoreQueryFilters()
                .Where(i => tenantIds.Contains(i.TenantId))
                .ToListAsync(cancellationToken))
            .ToDictionary(i => (i.TenantId, i.PeriodStartUtc));

        var targets = new List<(Guid TenantId, DateTime PeriodStart, DateTime PeriodEnd, Invoice? Existing, bool IsCurrentPeriod)>();

        foreach (var sub in subscriptions)
        {
            var monthCursor = new DateTime(sub.CurrentPeriodStartUtc.Year, sub.CurrentPeriodStartUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var upperBoundInclusive = currentMonthStart;
            if (sub.Status == SubscriptionStatus.Canceled && sub.CurrentPeriodEndUtc is { } endUtc)
            {
                var cancelMonth = new DateTime(endUtc.Year, endUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                if (cancelMonth < upperBoundInclusive)
                    upperBoundInclusive = cancelMonth;
            }

            while (monthCursor <= upperBoundInclusive)
            {
                var periodEnd = monthCursor.AddMonths(1);
                var existing = existingByKey.GetValueOrDefault((sub.TenantId, monthCursor));
                var isCurrentPeriod = monthCursor == currentMonthStart;

                // A row that has already finalized (Due or Paid) is done forever. What still needs
                // computing: a brand new period (no row yet, whichever status it will start at), the
                // current period's own existing row (always Upcoming, refreshed every call), or an
                // existing row still marked Upcoming even though it's no longer the current period -
                // that last case is the one-time rollover transition to Due.
                if (existing is null || isCurrentPeriod || existing.Status == InvoiceStatus.Upcoming)
                    targets.Add((sub.TenantId, monthCursor, periodEnd, existing, isCurrentPeriod));

                monthCursor = periodEnd;
            }
        }

        if (targets.Count == 0)
            return;

        var neededTenantIds = targets.Select(x => x.TenantId).Distinct().ToList();

        var pricingByTenant = (await _context.Tenants
                .Where(t => neededTenantIds.Contains(t.Id))
                .Select(t => new { t.Id, t.CountryCode })
                .ToListAsync(cancellationToken))
            .ToDictionary(t => t.Id, t => RegionalPricingCatalog.Resolve(t.CountryCode));

        var planIds = subscriptions.Where(s => neededTenantIds.Contains(s.TenantId)).Select(s => s.PlanId).Distinct().ToList();
        var plans = await _context.Plans.Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var planByTenant = subscriptions
            .Where(s => neededTenantIds.Contains(s.TenantId))
            .ToDictionary(s => s.TenantId, s => plans.GetValueOrDefault(s.PlanId));

        // A tenant's CURRENT user count, not a per-period reconstruction - see Invoice.UserCount's own
        // doc comment for why, same "snapshot, not history" limitation as SubscriptionAmountUsd.
        var userCountsByTenant = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId != null && neededTenantIds.Contains(u.TenantId!.Value))
            .GroupBy(u => u.TenantId!.Value)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

        var newInvoices = new List<Invoice>();

        foreach (var window in targets.GroupBy(x => (x.PeriodStart, x.PeriodEnd)))
        {
            var (periodStart, periodEnd) = window.Key;
            var windowTenantIds = window.Select(w => w.TenantId).ToList();

            var messageCountsByTenant = await _context.Messages.IgnoreQueryFilters()
                .Where(m => windowTenantIds.Contains(m.TenantId) && m.CreatedAt >= periodStart && m.CreatedAt < periodEnd)
                .GroupBy(m => m.TenantId)
                .Select(g => new { TenantId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

            var leadDiscoveryByTenant = await _context.LeadDiscoveryRuns.IgnoreQueryFilters()
                .Where(r => windowTenantIds.Contains(r.TenantId) && r.RanAtUtc >= periodStart && r.RanAtUtc < periodEnd)
                .GroupBy(r => r.TenantId)
                .Select(g => new { TenantId = g.Key, Runs = g.Count(), Leads = g.Sum(r => r.LeadsSaved), CostUsd = g.Sum(r => r.EstimatedCostUsd) })
                .ToDictionaryAsync(g => g.TenantId, cancellationToken);

            var aiInteractions = await _context.AiInteractions.IgnoreQueryFilters()
                .Where(a => windowTenantIds.Contains(a.TenantId) && a.CreatedAt >= periodStart && a.CreatedAt < periodEnd)
                .Select(a => new { a.TenantId, a.ModelUsed, a.PromptTokens, a.CompletionTokens })
                .ToListAsync(cancellationToken);
            var aiByTenant = aiInteractions
                .GroupBy(a => a.TenantId)
                .ToDictionary(g => g.Key, g => (
                    Count: g.Count(),
                    Usd: g.Sum(a => AiSpendEstimator.EstimateUsd(a.ModelUsed, a.PromptTokens, a.CompletionTokens))));

            var whatsAppByTenant = await _whatsAppSpend.GetForTenantsAsync(windowTenantIds, periodStart, periodEnd, cancellationToken);

            foreach (var item in window)
            {
                var plan = planByTenant.GetValueOrDefault(item.TenantId);
                var pricing = pricingByTenant.GetValueOrDefault(item.TenantId) ?? RegionalPricingCatalog.UsdDefault;

                var subscriptionUsd = plan is null ? 0m : plan.PriceMonthlyCents / 100m;
                var messagesSent = messageCountsByTenant.GetValueOrDefault(item.TenantId);
                var userCount = userCountsByTenant.GetValueOrDefault(item.TenantId);
                var discovery = leadDiscoveryByTenant.GetValueOrDefault(item.TenantId);
                var leadDiscoveryUsd = discovery?.CostUsd ?? 0m;
                var (aiCount, aiUsd) = aiByTenant.GetValueOrDefault(item.TenantId);
                var whatsApp = whatsAppByTenant.GetValueOrDefault(item.TenantId, WhatsAppSpend.Empty);
                var whatsAppUsd = whatsApp.EstimatedCostUsd;
                var totalUsd = Round(subscriptionUsd + leadDiscoveryUsd + whatsAppUsd + aiUsd);
                // Upcoming while it's the current, still-accruing period; Due the moment it isn't -
                // whether that's a brand new closed-month row or one rolling over out of Upcoming.
                var status = item.IsCurrentPeriod ? InvoiceStatus.Upcoming : InvoiceStatus.Due;

                if (item.Existing is { } invoice)
                {
                    // Refreshed in place - PaidAtUtc untouched (this path never reaches an
                    // already-Paid row, see the targets filter above).
                    invoice.Status = status;
                    invoice.PlanName = plan?.Name;
                    invoice.SubscriptionAmountUsd = subscriptionUsd;
                    invoice.MessagesSentCount = messagesSent;
                    invoice.MessageLimit = plan?.MaxMessagesPerMonth;
                    invoice.UserCount = userCount;
                    invoice.UserLimit = plan?.MaxUsers;
                    invoice.LeadDiscoveryAmountUsd = leadDiscoveryUsd;
                    invoice.LeadDiscoveryRunsCount = discovery?.Runs ?? 0;
                    invoice.LeadDiscoveryLeadsCount = discovery?.Leads ?? 0;
                    invoice.WhatsAppAmountUsd = whatsAppUsd;
                    invoice.WhatsAppBillableMessagesCount = whatsApp.BillableMessages;
                    invoice.AiConversationAmountUsd = aiUsd;
                    invoice.AiInteractionsCount = aiCount;
                    invoice.TotalAmountUsd = totalUsd;
                    invoice.CurrencyCode = pricing.CurrencyCode;
                    invoice.CurrencySymbol = pricing.CurrencySymbol;
                    invoice.SubscriptionAmountLocal = ToLocal(subscriptionUsd, pricing);
                    invoice.LeadDiscoveryAmountLocal = ToLocal(leadDiscoveryUsd, pricing);
                    invoice.WhatsAppAmountLocal = ToLocal(whatsAppUsd, pricing);
                    invoice.AiConversationAmountLocal = ToLocal(aiUsd, pricing);
                    invoice.TotalAmountLocal = ToLocal(totalUsd, pricing);
                    continue;
                }

                newInvoices.Add(new Invoice
                {
                    TenantId = item.TenantId,
                    PeriodStartUtc = item.PeriodStart,
                    PeriodEndUtc = item.PeriodEnd,
                    PlanName = plan?.Name,
                    SubscriptionAmountUsd = subscriptionUsd,
                    MessagesSentCount = messagesSent,
                    MessageLimit = plan?.MaxMessagesPerMonth,
                    UserCount = userCount,
                    UserLimit = plan?.MaxUsers,
                    LeadDiscoveryAmountUsd = leadDiscoveryUsd,
                    LeadDiscoveryRunsCount = discovery?.Runs ?? 0,
                    LeadDiscoveryLeadsCount = discovery?.Leads ?? 0,
                    WhatsAppAmountUsd = whatsAppUsd,
                    WhatsAppBillableMessagesCount = whatsApp.BillableMessages,
                    AiConversationAmountUsd = aiUsd,
                    AiInteractionsCount = aiCount,
                    TotalAmountUsd = totalUsd,
                    CurrencyCode = pricing.CurrencyCode,
                    CurrencySymbol = pricing.CurrencySymbol,
                    SubscriptionAmountLocal = ToLocal(subscriptionUsd, pricing),
                    LeadDiscoveryAmountLocal = ToLocal(leadDiscoveryUsd, pricing),
                    WhatsAppAmountLocal = ToLocal(whatsAppUsd, pricing),
                    AiConversationAmountLocal = ToLocal(aiUsd, pricing),
                    TotalAmountLocal = ToLocal(totalUsd, pricing),
                    Status = status
                });
            }
        }

        _context.Invoices.AddRange(newInvoices);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static PlatformInvoiceDetailDto ToDetailDto(Invoice i, string tenantName) => new(
        i.Id, i.TenantId, tenantName, i.PeriodStartUtc, i.PeriodEndUtc, i.PlanName, i.Status, i.PaidAtUtc,
        i.SubscriptionAmountUsd, i.SubscriptionAmountLocal, i.MessagesSentCount, i.MessageLimit, i.UserCount, i.UserLimit,
        i.LeadDiscoveryAmountUsd, i.LeadDiscoveryAmountLocal, i.LeadDiscoveryRunsCount, i.LeadDiscoveryLeadsCount,
        i.WhatsAppAmountUsd, i.WhatsAppAmountLocal, i.WhatsAppBillableMessagesCount,
        i.AiConversationAmountUsd, i.AiConversationAmountLocal, i.AiInteractionsCount,
        i.TotalAmountUsd, i.TotalAmountLocal,
        i.CurrencyCode, i.CurrencySymbol);

    /// <summary>Six decimal places, the same precision PlatformUsageService/TenantChargesService keep
    /// for the same underlying estimates.</summary>
    private static decimal ToLocal(decimal usd, RegionalPricing pricing) => Round(usd * pricing.RateToUsd);

    private static decimal Round(decimal amount) => Math.Round(amount, 6, MidpointRounding.AwayFromZero);
}
