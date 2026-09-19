using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// Warns a tenant before its plan's billing period ends, so a renewal charge - or a plan that cannot renew - is never a
/// surprise. Two notices per period: seven days out, and one day out. Each shows in the tenant's notifications, and goes by email
/// and WhatsApp as the other billing alerts do. Safe to run as often as you like: a notice is raised once per period.
/// </summary>
public interface IPlanExpiryNoticeService
{
    /// <summary>Returns how many notices were raised.</summary>
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}

public class PlanExpiryNoticeService : IPlanExpiryNoticeService
{
    /// <summary>How far ahead the first notice goes out.</summary>
    public const int FirstNoticeDays = 7;

    /// <summary>The last notice, the day before.</summary>
    public const int FinalNoticeDays = 1;

    private readonly IApplicationDbContext _context;
    private readonly ITenantNotifier _notifier;
    private readonly IPricingService _pricing;
    private readonly IDateTimeProvider _dateTime;

    public PlanExpiryNoticeService(IApplicationDbContext context, ITenantNotifier notifier, IPricingService pricing, IDateTimeProvider dateTime)
    {
        _context = context;
        _notifier = notifier;
        _pricing = pricing;
        _dateTime = dateTime;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var horizon = now.AddDays(FirstNoticeDays);

        // Only a running, paid-for plan whose period ends inside the window. One already past its end is the renewal job's business.
        var due = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.Status == SubscriptionStatus.Active && s.PlanId != null && s.CurrentPeriodEndUtc != null
                        && s.CurrentPeriodEndUtc > now && s.CurrentPeriodEndUtc <= horizon)
            .Join(_context.Tenants, s => s.TenantId, t => t.Id, (s, t) => new { s, t })
            .Join(_context.Plans, x => x.s.PlanId, p => (Guid?)p.Id, (x, p) => new
            {
                x.s.TenantId, x.t.CountryCode, x.t.StateCode, PlanId = p.Id, PlanName = p.Name, EndsAtUtc = x.s.CurrentPeriodEndUtc!.Value
            })
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
            return 0;

        var prices = await CatalogPricing.LoadPlanPricesAsync(_context, due.Select(d => d.PlanId).Distinct().ToList(), cancellationToken);

        var raised = 0;
        foreach (var d in due)
        {
            var daysLeft = (d.EndsAtUtc - now).TotalDays;
            var kind = daysLeft <= FinalNoticeDays ? TenantNotificationKind.PlanExpiring1 : TenantNotificationKind.PlanExpiring7;

            var region = RegionalPricingCatalog.Resolve(d.CountryCode);
            var quote = _pricing.Quote(prices.GetValueOrDefault(d.PlanId)?.GetValueOrDefault(region.CountryCode), d.CountryCode, d.StateCode);

            var (title, body) = Compose(d.PlanName, d.EndsAtUtc, daysLeft, quote);

            // One per period: the period's end date is the episode, so the next period's notices are new ones.
            var episode = d.EndsAtUtc.ToString("yyyyMMdd");
            if (await _notifier.NotifyAsync(new TenantNotificationRequest(d.TenantId, kind, null, episode, title, body, AlsoWhatsApp: true), cancellationToken))
                raised++;
        }

        return raised;
    }

    private static (string Title, string Body) Compose(string planName, DateTime endsAtUtc, double daysLeft, PriceQuote? quote)
    {
        var date = endsAtUtc.ToString("d MMM yyyy");
        var when = daysLeft <= FinalNoticeDays ? "tomorrow" : $"in {Math.Ceiling(daysLeft)} days";

        // A plan with no price in the tenant's country cannot renew - say so plainly, and what to do about it.
        if (quote is null)
        {
            return (
                $"Your {planName} plan ends {when}",
                $"Your {planName} plan ends on {date} and can't renew automatically. Please contact us before then to keep your service running.");
        }

        var price = Money(quote.CurrencySymbol, quote.Subtotal);
        var total = Money(quote.CurrencySymbol, quote.Total);
        var charge = quote.Tax > 0 ? $"{total} ({price} plus tax)" : total;

        return (
            $"Your {planName} plan renews {when}",
            $"Your {planName} plan renews on {date}. {charge} will be charged automatically and your quota renewed - there's nothing you need to do to continue.");
    }

    private static string Money(string symbol, decimal amount) =>
        $"{symbol}{(amount == decimal.Truncate(amount) ? amount.ToString("0") : amount.ToString("0.00"))}";
}
