using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>What a row of the Payments screen is. <c>Paid</c> is money that actually moved (a plan subscription, a credit pack, or a
/// refund, which is negative). The rest are monthly subscriptions that have NOT been charged yet, listed so a subscription is
/// visible before its first payment exists: <c>Upcoming</c> (the next charge is scheduled), <c>NotScheduled</c> (an operator set
/// the plan and there is no billing period yet) and <c>NoPrice</c> (the plan has no price in the tenant's country, so it cannot be
/// charged or renewed there).</summary>
public static class PaymentRowStatus
{
    public const string Paid = "Paid";
    public const string Upcoming = "Upcoming";
    public const string NotScheduled = "NotScheduled";
    public const string NoPrice = "NoPrice";
}

/// <summary>One row of the Payments screen. Amounts are in that tenant's own currency (snapshotted when it was charged, or - for an
/// upcoming charge - what it would be charged today: the price set for its country plus tax); <see cref="AmountInr"/> is the total
/// in rupees. For an upcoming row <see cref="PaidAtUtc"/> is null and <see cref="DueAtUtc"/> is the next charge date.</summary>
public record PlatformPaymentListItemDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string Kind,
    string Description,
    int AmountCents,
    string CurrencyCode,
    string CurrencySymbol,
    decimal LocalAmount,
    string Provider,
    DateTime? PaidAtUtc,
    Guid? RefundOfPaymentId,
    string? CountryCode = null,
    string? StateCode = null,
    decimal TaxLocal = 0,
    decimal TotalLocal = 0,
    IReadOnlyList<TaxLineDto>? TaxLines = null,
    decimal AmountInr = 0,
    string Status = PaymentRowStatus.Paid,
    DateTime? DueAtUtc = null);

public record PlatformPaymentQuery : PagedRequest
{
    public PaymentKind? Kind { get; init; }

    public Guid? TenantId { get; init; }
}

/// <summary>The Platform Admin Console's Payments screen: every payment across tenants, with each subscribed tenant's upcoming
/// monthly charge at the top. Read-only - money moves through checkout, credit purchases and refunds, never from this list.
/// Replaces the old usage-invoice screen: with prepaid billing nothing is billed afterwards, so there is nothing to invoice, only
/// payments to look back on and charges to look forward to.</summary>
public interface IPlatformPaymentService
{
    Task<PagedResult<PlatformPaymentListItemDto>> GetPagedAsync(PlatformPaymentQuery query, CancellationToken cancellationToken = default);
}

public class PlatformPaymentService : IPlatformPaymentService
{
    private readonly IApplicationDbContext _context;
    private readonly IPricingService _pricing;

    public PlatformPaymentService(IApplicationDbContext context, IPricingService pricing)
    {
        _context = context;
        _pricing = pricing;
    }

    /// <summary>
    /// One list, two sources: the upcoming monthly charges first (one per subscribed tenant, soonest first), then the payments
    /// newest first. Paging runs across both as if they were one list. Upcoming rows are subscription charges, so they appear for
    /// no filter or the Subscription type, and never for credit packs or refunds.
    /// </summary>
    public async Task<PagedResult<PlatformPaymentListItemDto>> GetPagedAsync(PlatformPaymentQuery query, CancellationToken cancellationToken = default)
    {
        var upcoming = query.Kind is null or PaymentKind.Subscription
            ? await GetUpcomingAsync(query, cancellationToken)
            : new List<PlatformPaymentListItemDto>();

        var payments = _context.Payments.IgnoreQueryFilters()
            .Join(_context.Tenants, p => p.TenantId, t => t.Id, (p, t) => new { p, t });

        if (query.Kind is { } kind)
            payments = payments.Where(x => x.p.Kind == kind);

        if (query.TenantId is { } tenantId)
            payments = payments.Where(x => x.p.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            payments = payments.Where(x => x.t.Name.Contains(search) || x.t.Slug.Contains(search) || x.p.PlanName.Contains(search));
        }

        var paymentCount = await payments.CountAsync(cancellationToken);

        var offset = (query.Page - 1) * query.PageSize;
        var fromUpcoming = upcoming.Skip(offset).Take(query.PageSize).ToList();
        var room = query.PageSize - fromUpcoming.Count;

        var fromPayments = new List<PlatformPaymentListItemDto>();
        if (room > 0)
        {
            var rows = await payments
                .OrderByDescending(x => x.p.PaidAtUtc).ThenBy(x => x.t.Name)
                .Skip(Math.Max(0, offset - upcoming.Count))
                .Take(room)
                .ToListAsync(cancellationToken);

            fromPayments = rows
                .Select(x => new PlatformPaymentListItemDto(
                    x.p.Id, x.p.TenantId, x.t.Name, x.p.Kind.ToString(), x.p.PlanName, x.p.AmountCents,
                    x.p.CurrencyCode, x.p.CurrencySymbol, x.p.LocalAmount, x.p.Provider, x.p.PaidAtUtc, x.p.RefundOfPaymentId,
                    x.p.CountryCode, x.p.StateCode, x.p.TaxLocal, x.p.TotalPaidLocal, PaymentDto.ParseTaxLines(x.p.TaxLinesJson), x.p.AmountInr))
                .ToList();
        }

        return new PagedResult<PlatformPaymentListItemDto>(
            fromUpcoming.Concat(fromPayments).ToList(), upcoming.Count + paymentCount, query.Page, query.PageSize);
    }

    /// <summary>Every tenant that has a plan, as the charge it is due: the price set for its own country with tax on top - the same
    /// figure a renewal charges. Sorted with the soonest due first; those with no schedule or no price after.</summary>
    private async Task<List<PlatformPaymentListItemDto>> GetUpcomingAsync(PlatformPaymentQuery query, CancellationToken cancellationToken)
    {
        var subscribed = _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.PlanId != null)
            .Join(_context.Tenants, s => s.TenantId, t => t.Id, (s, t) => new { s, t })
            .Join(_context.Plans, x => x.s.PlanId, p => (Guid?)p.Id, (x, p) => new { x.s, x.t, p });

        if (query.TenantId is { } tenantId)
            subscribed = subscribed.Where(x => x.t.Id == tenantId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            subscribed = subscribed.Where(x => x.t.Name.Contains(search) || x.t.Slug.Contains(search) || x.p.Name.Contains(search));
        }

        var rows = await subscribed
            .Select(x => new
            {
                SubscriptionId = x.s.Id, TenantId = x.t.Id, TenantName = x.t.Name, PlanId = x.p.Id, PlanName = x.p.Name,
                x.s.CurrentPeriodEndUtc, x.t.CountryCode, x.t.StateCode
            })
            .ToListAsync(cancellationToken);

        var prices = await CatalogPricing.LoadPlanPricesAsync(_context, rows.Select(x => x.PlanId).Distinct().ToList(), cancellationToken);

        return rows
            .Select(x =>
            {
                var region = RegionalPricingCatalog.Resolve(x.CountryCode);
                var quote = _pricing.Quote(prices.GetValueOrDefault(x.PlanId)?.GetValueOrDefault(region.CountryCode), x.CountryCode, x.StateCode);

                var status = quote is null ? PaymentRowStatus.NoPrice
                    : x.CurrentPeriodEndUtc is null ? PaymentRowStatus.NotScheduled
                    : PaymentRowStatus.Upcoming;

                return new PlatformPaymentListItemDto(
                    x.SubscriptionId, x.TenantId, x.TenantName, PaymentKind.Subscription.ToString(), x.PlanName,
                    quote?.SubtotalUsdCents ?? 0, region.CurrencyCode, region.CurrencySymbol, quote?.Subtotal ?? 0m,
                    string.Empty, null, null,
                    region.CountryCode, quote?.StateCode, quote?.Tax ?? 0m, quote?.Total ?? 0m, quote?.TaxLines ?? Array.Empty<TaxLineDto>(),
                    quote?.TotalInr ?? 0m, status, x.CurrentPeriodEndUtc);
            })
            .OrderBy(x => x.Status == PaymentRowStatus.Upcoming ? 0 : 1)
            .ThenBy(x => x.DueAtUtc)
            .ThenBy(x => x.TenantName)
            .ToList();
    }
}
