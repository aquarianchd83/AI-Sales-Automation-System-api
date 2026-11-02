using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Money for one slice of the report, all in rupees at the rate of the day each payment was taken. <paramref name="Gross"/> is what
/// tenants paid, tax included; <paramref name="Refunds"/> is what was returned (a positive figure); <paramref name="Tax"/> is the tax inside
/// the net, so <paramref name="NetExTax"/> is what the platform actually keeps.</summary>
public record RevenueFiguresDto(decimal Gross, decimal Refunds, decimal Net, decimal Tax, decimal NetExTax, int Payments, int RefundCount);

public record RevenueByKindDto(string Kind, decimal Amount, int Count);

public record RevenueMonthDto(string Month, decimal Subscription, decimal CreditPack, decimal Refunds, decimal Net);

public record RevenueTenantDto(Guid TenantId, string TenantName, decimal Net, int Payments, DateTime? LastPaidAtUtc);

public record RevenueCountryDto(string CountryCode, string CurrencyCode, decimal Net, int Payments);

public record PlatformRevenueReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    RevenueFiguresDto Totals,
    RevenueFiguresDto Previous,
    IReadOnlyList<RevenueByKindDto> ByKind,
    IReadOnlyList<RevenueMonthDto> Months,
    IReadOnlyList<RevenueTenantDto> TopTenants,
    IReadOnlyList<RevenueCountryDto> Countries);

/// <summary>From and To are inclusive calendar days (UTC). Both optional: the default is the last twelve months, this one included.</summary>
public record PlatformRevenueQuery
{
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    /// <summary>Only this tenant's payments. With no From, the report then starts at the tenant's first payment instead of twelve months back, so the totals are for its whole history.</summary>
    public Guid? TenantId { get; init; }
}

/// <summary>The Platform Admin Console's Revenue report: what tenants paid for plans and credit packs, less refunds, in rupees. Read-only,
/// and built only from <c>Payments</c> - money that actually moved. An upcoming subscription charge is not revenue until it is taken, and
/// credits a platform wallet spends on a tenant's behalf are a cost, shown on Service margins.</summary>
public interface IPlatformRevenueService
{
    Task<PlatformRevenueReportDto> GetAsync(PlatformRevenueQuery query, CancellationToken cancellationToken = default);
}

public class PlatformRevenueService : IPlatformRevenueService
{
    private const int TopTenantCount = 10;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;

    public PlatformRevenueService(IApplicationDbContext context, IDateTimeProvider dateTime)
    {
        _context = context;
        _dateTime = dateTime;
    }

    private sealed record Row(Guid TenantId, string TenantName, PaymentKind Kind, decimal AmountInr, decimal TaxInr, string? CountryCode, string CurrencyCode, DateTime PaidAtUtc);

    public async Task<PlatformRevenueReportDto> GetAsync(PlatformRevenueQuery query, CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var firstPayment = query.TenantId is { } only && query.From is null
            ? await _context.Payments.AsNoTracking().IgnoreQueryFilters().Where(p => p.TenantId == only).MinAsync(p => (DateTime?)p.PaidAtUtc, cancellationToken)
            : null;
        var from = query.From?.Date ?? (query.TenantId is not null ? (firstPayment?.Date ?? thisMonth) : thisMonth.AddMonths(-11));
        var to = (query.To?.Date ?? now.Date);
        if (to < from)
            (from, to) = (to, from);
        from = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toExclusive = DateTime.SpecifyKind(to, DateTimeKind.Utc).AddDays(1);

        // The period just before, the same length, so every headline figure can say whether it is up or down.
        var span = toExclusive - from;
        var previousFrom = from - span;

        var rows = await (
            from p in _context.Payments.AsNoTracking().IgnoreQueryFilters()
            join t in _context.Tenants.AsNoTracking() on p.TenantId equals t.Id
            where p.PaidAtUtc >= previousFrom && p.PaidAtUtc < toExclusive && (query.TenantId == null || p.TenantId == query.TenantId)
            select new
            {
                p.TenantId, TenantName = t.Name, p.Kind, p.AmountInr, p.TaxLocal, p.TotalLocal, p.LocalAmount, p.CountryCode, p.CurrencyCode, p.PaidAtUtc
            })
            .ToListAsync(cancellationToken);

        // Tax is stored in the tenant's currency; AmountInr is the whole payment in rupees, so tax in rupees is the same share of it.
        var all = rows.Select(r =>
        {
            var paidLocal = r.TotalLocal != 0 ? r.TotalLocal : r.LocalAmount;
            var taxInr = paidLocal != 0 ? r.AmountInr * r.TaxLocal / paidLocal : 0m;
            return new Row(r.TenantId, r.TenantName, r.Kind, r.AmountInr, taxInr, r.CountryCode, r.CurrencyCode, r.PaidAtUtc);
        }).ToList();

        var current = all.Where(r => r.PaidAtUtc >= from).ToList();
        var previous = all.Where(r => r.PaidAtUtc < from).ToList();

        var byKind = new[] { PaymentKind.Subscription, PaymentKind.CreditPack, PaymentKind.Refund }
            .Select(k => new RevenueByKindDto(
                k.ToString(), Round(current.Where(r => r.Kind == k).Sum(r => Math.Abs(r.AmountInr))), current.Count(r => r.Kind == k)))
            .ToList();

        return new PlatformRevenueReportDto(
            from, toExclusive.AddTicks(-1), Figures(current), Figures(previous), byKind, Months(current, from, to),
            TopTenants(current), Countries(current));
    }

    private static RevenueFiguresDto Figures(IReadOnlyCollection<Row> rows)
    {
        var paid = rows.Where(r => r.Kind != PaymentKind.Refund).ToList();
        var refunded = rows.Where(r => r.Kind == PaymentKind.Refund).ToList();

        // A refund row carries negative amounts, so its magnitude is what went back.
        var gross = paid.Sum(r => r.AmountInr);
        var refunds = refunded.Sum(r => Math.Abs(r.AmountInr));
        var tax = paid.Sum(r => r.TaxInr) - refunded.Sum(r => Math.Abs(r.TaxInr));
        var net = gross - refunds;

        return new RevenueFiguresDto(Round(gross), Round(refunds), Round(net), Round(tax), Round(net - tax), paid.Count, refunded.Count);
    }

    private static List<RevenueMonthDto> Months(IReadOnlyCollection<Row> rows, DateTime from, DateTime to)
    {
        var months = new List<RevenueMonthDto>();
        for (var m = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc); m <= to; m = m.AddMonths(1))
        {
            var inMonth = rows.Where(r => r.PaidAtUtc.Year == m.Year && r.PaidAtUtc.Month == m.Month).ToList();
            var subscription = inMonth.Where(r => r.Kind == PaymentKind.Subscription).Sum(r => r.AmountInr);
            var pack = inMonth.Where(r => r.Kind == PaymentKind.CreditPack).Sum(r => r.AmountInr);
            var refunds = inMonth.Where(r => r.Kind == PaymentKind.Refund).Sum(r => Math.Abs(r.AmountInr));
            months.Add(new RevenueMonthDto($"{m:yyyy-MM}", Round(subscription), Round(pack), Round(refunds), Round(subscription + pack - refunds)));
        }

        return months;
    }

    private static List<RevenueTenantDto> TopTenants(IReadOnlyCollection<Row> rows) => rows
        .GroupBy(r => (r.TenantId, r.TenantName))
        .Select(g => new RevenueTenantDto(
            g.Key.TenantId, g.Key.TenantName, Round(g.Sum(r => r.AmountInr)), g.Count(r => r.Kind != PaymentKind.Refund),
            g.Where(r => r.Kind != PaymentKind.Refund).Select(r => (DateTime?)r.PaidAtUtc).DefaultIfEmpty().Max()))
        .OrderByDescending(t => t.Net)
        .Take(TopTenantCount)
        .ToList();

    private static List<RevenueCountryDto> Countries(IReadOnlyCollection<Row> rows) => rows
        .GroupBy(r => (Country: string.IsNullOrWhiteSpace(r.CountryCode) ? "-" : r.CountryCode!.ToUpperInvariant(), r.CurrencyCode))
        .Select(g => new RevenueCountryDto(g.Key.Country, g.Key.CurrencyCode, Round(g.Sum(r => r.AmountInr)), g.Count(r => r.Kind != PaymentKind.Refund)))
        .OrderByDescending(c => c.Net)
        .ToList();

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
