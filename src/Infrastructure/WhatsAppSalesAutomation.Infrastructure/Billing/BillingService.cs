using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using TenantSubscription = WhatsAppSalesAutomation.Domain.Entities.Billing.Subscription;
using Plan = WhatsAppSalesAutomation.Domain.Entities.Billing.Plan;
using Payment = WhatsAppSalesAutomation.Domain.Entities.Billing.Payment;

namespace WhatsAppSalesAutomation.Infrastructure.Billing;

/// <summary>Implementation of <see cref="IBillingService"/> - see its own doc comment for why this
/// simulates payment rather than calling a real gateway (Stripe pulled out, Razorpay not wired in yet,
/// India-first launch). Everything here is plain business logic against IApplicationDbContext, no
/// external SDK involved at all - unlike the WhatsApp/AI providers, there is no "real" implementation
/// this stands in for behind a factory; this class itself is the placeholder, swapped out (or grown a
/// sibling behind the same interface) once a real gateway exists.</summary>
public class BillingService : IBillingService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantJobProvisioner _jobProvisioner;

    public BillingService(
        IApplicationDbContext context,
        ITenantContext tenantContext,
        IDateTimeProvider dateTime,
        ITenantJobProvisioner jobProvisioner)
    {
        _context = context;
        _tenantContext = tenantContext;
        _dateTime = dateTime;
        _jobProvisioner = jobProvisioner;
    }

    /// <summary>Resolves the region to price in: the explicit <paramref name="countryCode"/> when the
    /// caller supplied one (BillingController threads through an anonymous ?country= query param, for
    /// a not-yet-signed-up visitor previewing pricing), else the ambient tenant's own stored
    /// Tenant.CountryCode when this is an authenticated call, else no country at all - which
    /// RegionalPricingCatalog.Resolve treats the same as an unmatched code (USD).</summary>
    public async Task<IReadOnlyList<PlanDto>> GetPlansAsync(string? countryCode = null, CancellationToken cancellationToken = default)
    {
        var resolvedCountryCode = countryCode;
        if (resolvedCountryCode is null && _tenantContext.TenantId is { } tenantId)
        {
            resolvedCountryCode = await _context.Tenants
                .Where(t => t.Id == tenantId)
                .Select(t => t.CountryCode)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var pricing = RegionalPricingCatalog.Resolve(resolvedCountryCode);

        var plans = await _context.Plans
            .Where(p => p.IsActive)
            .OrderBy(p => p.PriceMonthlyCents)
            .ToListAsync(cancellationToken);

        return plans.Select(p => new PlanDto(
            p.Id, p.Code, p.Name, p.MaxUsers, p.MaxMessagesPerMonth, p.MaxCampaigns, p.MaxKnowledgeBaseArticles,
            p.PriceMonthlyCents, pricing.CurrencyCode, pricing.CurrencySymbol,
            Math.Round(p.PriceMonthlyCents / 100m * pricing.RateToUsd, 2))).ToList();
    }

    public Task<IReadOnlyList<RegionDto>> GetRegionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RegionDto>>(
            RegionalPricingCatalog.All.Select(r => new RegionDto(r.CountryCode, r.CountryName, r.CurrencyCode, r.CurrencySymbol)).ToList());

    public async Task<SubscriptionDto?> GetSubscriptionForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters(): the calling tenant's own ambient TenantId already equals tenantId in
        // every real call site, but this reads the same way regardless of ambient state - see
        // PlanLimitsService's identical reasoning.
        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (subscription is null)
            return null;

        string? planName = null;
        if (subscription.PlanId is { } planId)
        {
            var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
            planName = plan?.Name;
        }

        return new SubscriptionDto(
            subscription.PlanId, planName, subscription.Status.ToString(),
            subscription.CurrentPeriodStartUtc, subscription.CurrentPeriodEndUtc);
    }

    /// <summary>Simulates a successful payment: upserts the tenant's Subscription (same "find or
    /// create, exactly one row" shape PlatformTenantService.OverridePlanAsync already uses) with a
    /// fresh one-month period starting now, flips the tenant Active, and records one Payment
    /// snapshotting the plan's price converted into the tenant's own currency - the same conversion
    /// GetPlansAsync already does for display, done again here since a payment is what actually
    /// "locks in" that amount rather than just previewing it.</summary>
    public async Task<SubscriptionDto> ChoosePlanAsync(Guid tenantId, Guid planId, CancellationToken cancellationToken = default)
    {
        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), planId);

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (subscription is null)
        {
            subscription = new TenantSubscription { TenantId = tenantId };
            _context.Subscriptions.Add(subscription);
        }

        var now = _dateTime.UtcNow;
        subscription.PlanId = plan.Id;
        subscription.Status = SubscriptionStatus.Active;
        subscription.CurrentPeriodStartUtc = now;
        subscription.CurrentPeriodEndUtc = now.AddMonths(1);

        tenant.Status = TenantStatus.Active;

        var pricing = RegionalPricingCatalog.Resolve(tenant.CountryCode);
        _context.Payments.Add(new Payment
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            PlanName = plan.Name,
            AmountCents = plan.PriceMonthlyCents,
            CurrencyCode = pricing.CurrencyCode,
            CurrencySymbol = pricing.CurrencySymbol,
            LocalAmount = Math.Round(plan.PriceMonthlyCents / 100m * pricing.RateToUsd, 2),
            Provider = "Simulated",
            PaidAtUtc = now
        });

        await _context.SaveChangesAsync(cancellationToken);

        // Subscribing is the one non-operator path that moves a tenant's status, so it re-syncs its
        // background jobs like the Platform Admin Console's own actions do: a tenant that was suspended
        // for non-payment gets its campaigns sending again as soon as it pays, not up to an hour later
        // when the reconcile pass next runs.
        await _jobProvisioner.SyncTenantAsync(tenantId, cancellationToken);

        return new SubscriptionDto(
            subscription.PlanId, plan.Name, subscription.Status.ToString(),
            subscription.CurrentPeriodStartUtc, subscription.CurrentPeriodEndUtc);
    }

    public async Task<IReadOnlyList<PaymentDto>> GetPaymentHistoryForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var payments = await _context.Payments.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.PaidAtUtc)
            .ToListAsync(cancellationToken);

        return payments.Select(p => new PaymentDto(
            p.Id, p.PlanName, p.AmountCents, p.CurrencyCode, p.CurrencySymbol, p.LocalAmount, p.Provider, p.PaidAtUtc)).ToList();
    }
}
