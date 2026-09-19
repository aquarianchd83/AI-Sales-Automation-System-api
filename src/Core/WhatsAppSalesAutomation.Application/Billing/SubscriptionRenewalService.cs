using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// The periodic billing pass: renews every active subscription whose period has ended (a fresh payment
/// and a fresh quota allocation), then writes off whatever has expired. Payment is simulated, so a
/// renewal always succeeds - when a real gateway arrives, a failed charge is where a subscription moves
/// to PastDue instead of being renewed here. Safe to run as often as you like: allocation is idempotent
/// per period, and a subscription renewed a moment ago is no longer due.
/// </summary>
public interface ISubscriptionRenewalService
{
    /// <summary>Returns (subscriptions renewed, grants expired).</summary>
    Task<(int Renewed, int Expired)> RunAsync(CancellationToken cancellationToken = default);
}

public class SubscriptionRenewalService : ISubscriptionRenewalService
{
    private readonly IApplicationDbContext _context;
    private readonly IQuotaLedgerService _ledger;
    private readonly IDateTimeProvider _dateTime;
    private readonly IPricingService _pricing;

    public SubscriptionRenewalService(IApplicationDbContext context, IQuotaLedgerService ledger, IDateTimeProvider dateTime, IPricingService pricing)
    {
        _pricing = pricing;
        _context = context;
        _ledger = ledger;
        _dateTime = dateTime;
    }

    public async Task<(int Renewed, int Expired)> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;

        var due = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.Status == SubscriptionStatus.Active && s.PlanId != null && s.CurrentPeriodEndUtc != null && s.CurrentPeriodEndUtc <= now)
            .ToListAsync(cancellationToken);

        var renewed = 0;
        foreach (var subscription in due)
        {
            var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == subscription.PlanId, cancellationToken);
            var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == subscription.TenantId, cancellationToken);
            if (plan is null || tenant is null)
                continue;

            // The new period starts where the last one ended. If the platform was down for whole months,
            // those months are skipped rather than billed - nobody got service for them.
            var start = subscription.CurrentPeriodEndUtc!.Value;
            while (start.AddMonths(1) <= now)
                start = start.AddMonths(1);
            var end = start.AddMonths(1);

            // The price set for the tenant's country today, plus tax. A plan with no price there is not sold there, so it
            // cannot renew - the subscription is left as it is for the operator to sort out, and tried again next pass.
            var region = RegionalPricingCatalog.Resolve(tenant.CountryCode);
            var planPrice = (await CatalogPricing.LoadPlanPricesAsync(_context, new[] { plan.Id }, cancellationToken))
                .GetValueOrDefault(plan.Id)?.GetValueOrDefault(region.CountryCode);
            var quote = _pricing.Quote(planPrice, tenant.CountryCode, tenant.StateCode);
            if (quote is null)
                continue;

            var payment = PaymentFactory.For(tenant.Id, PaymentKind.Subscription, plan.Name, quote, now, planId: plan.Id, periodStartUtc: start, periodEndUtc: end);
            _context.Payments.Add(payment);
            subscription.CurrentPeriodStartUtc = start;
            subscription.CurrentPeriodEndUtc = end;
            await _context.SaveChangesAsync(cancellationToken);

            await _ledger.AllocatePlanQuotaAsync(tenant.Id, plan.Id, start, end, payment.Id, cancellationToken);
            renewed++;
        }

        var expired = await _ledger.ExpireDueAsync(cancellationToken);
        return (renewed, expired);
    }
}
