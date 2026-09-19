using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using TenantSubscription = WhatsAppSalesAutomation.Domain.Entities.Billing.Subscription;
using Plan = WhatsAppSalesAutomation.Domain.Entities.Billing.Plan;
using Payment = WhatsAppSalesAutomation.Domain.Entities.Billing.Payment;
using CreditPack = WhatsAppSalesAutomation.Domain.Entities.Billing.CreditPack;

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
    private readonly IQuotaLedgerService _quota;

    public BillingService(
        IApplicationDbContext context,
        ITenantContext tenantContext,
        IDateTimeProvider dateTime,
        ITenantJobProvisioner jobProvisioner,
        IQuotaLedgerService quota)
    {
        _quota = quota;
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

        var planIds = plans.Select(p => p.Id).ToList();
        var pricesByPlan = await CatalogPricing.LoadPlanPricesAsync(_context, planIds, cancellationToken);
        var quotasByPlan = (await _context.PlanQuotas.Where(q => planIds.Contains(q.PlanId)).ToListAsync(cancellationToken))
            .GroupBy(q => q.PlanId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IncludedQuotaDto>)g.OrderBy(q => q.QuotaType).Select(q => new IncludedQuotaDto(q.QuotaType, q.IncludedUnits)).ToList());

        return plans.Select(p => new PlanDto(
            p.Id, p.Code, p.Name, p.MaxUsers, p.MaxMessagesPerMonth, p.MaxCampaigns, p.MaxKnowledgeBaseArticles, p.MaxLeadDiscoveryBatchSize,
            p.PriceMonthlyCents, pricing.CurrencyCode, pricing.CurrencySymbol,
            CatalogPricing.Local(p.PriceMonthlyCents, pricing, pricesByPlan.GetValueOrDefault(p.Id)),
            quotasByPlan.GetValueOrDefault(p.Id) ?? Array.Empty<IncludedQuotaDto>())).ToList();
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
        var planPrices = (await CatalogPricing.LoadPlanPricesAsync(_context, new[] { plan.Id }, cancellationToken)).GetValueOrDefault(plan.Id);
        var (usdCents, localAmount) = CatalogPricing.Charge(plan.PriceMonthlyCents, pricing, planPrices);
        var payment = new Payment
        {
            TenantId = tenantId,
            Kind = PaymentKind.Subscription,
            PlanId = plan.Id,
            PlanName = plan.Name,
            AmountCents = usdCents,
            CurrencyCode = pricing.CurrencyCode,
            CurrencySymbol = pricing.CurrencySymbol,
            LocalAmount = localAmount,
            Provider = "Simulated",
            PaidAtUtc = now
        };
        _context.Payments.Add(payment);

        await _context.SaveChangesAsync(cancellationToken);

        // A new paid period replaces the old one: whatever is left of the previous period's included quota
        // is forfeited so two periods' allowances can never stack. Purchased credits are untouched.
        await _quota.ForfeitPlanAllocationsAsync(tenantId, "Replaced by a new plan period", cancellationToken);
        await _quota.AllocatePlanQuotaAsync(tenantId, plan.Id, now, now.AddMonths(1), payment.Id, cancellationToken);

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
            p.Id, p.PlanName, p.AmountCents, p.CurrencyCode, p.CurrencySymbol, p.LocalAmount, p.Provider, p.PaidAtUtc, p.Kind.ToString())).ToList();
    }

    public async Task<IReadOnlyList<CreditPackDto>> GetCreditPacksAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var countryCode = await _context.Tenants.Where(t => t.Id == tenantId).Select(t => t.CountryCode).FirstOrDefaultAsync(cancellationToken);
        var pricing = RegionalPricingCatalog.Resolve(countryCode);

        var packs = await _context.CreditPacks.Where(p => p.IsActive).ToListAsync(cancellationToken);
        var pricesByPack = await CatalogPricing.LoadPackPricesAsync(_context, packs.Select(p => p.Id).ToList(), cancellationToken);
        return packs
            .OrderBy(p => p.QuotaType).ThenBy(p => p.Units)
            .Select(p => new CreditPackDto(p.Id, p.QuotaType, p.Name, p.Units, p.PriceCents, pricing.CurrencyCode, pricing.CurrencySymbol,
                CatalogPricing.Local(p.PriceCents, pricing, pricesByPack.GetValueOrDefault(p.Id))))
            .ToList();
    }

    public async Task<PaymentDto> PurchaseCreditPackAsync(Guid tenantId, Guid packId, CancellationToken cancellationToken = default)
    {
        var pack = await _context.CreditPacks.FirstOrDefaultAsync(p => p.Id == packId && p.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(CreditPack), packId);

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

        var subscription = await _context.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        var canBuy = subscription is { PlanId: not null } && subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue
                     && tenant.Status is TenantStatus.Active or TenantStatus.Trial;
        if (!canBuy)
            throw new ConflictException("Credits can only be bought while you have an active plan. Choose a plan first.");

        var now = _dateTime.UtcNow;
        var pricing = RegionalPricingCatalog.Resolve(tenant.CountryCode);
        var packPrices = (await CatalogPricing.LoadPackPricesAsync(_context, new[] { pack.Id }, cancellationToken)).GetValueOrDefault(pack.Id);
        var (usdCents, localAmount) = CatalogPricing.Charge(pack.PriceCents, pricing, packPrices);
        var payment = new Payment
        {
            TenantId = tenantId,
            Kind = PaymentKind.CreditPack,
            CreditPackId = pack.Id,
            PlanName = pack.Name,
            AmountCents = usdCents,
            CurrencyCode = pricing.CurrencyCode,
            CurrencySymbol = pricing.CurrencySymbol,
            LocalAmount = localAmount,
            Provider = "Simulated",
            PaidAtUtc = now
        };
        _context.Payments.Add(payment);
        await _context.SaveChangesAsync(cancellationToken);

        // Idempotent per payment id, so a retry after a failure here can never grant twice.
        await _quota.GrantCreditsAsync(tenantId, pack, payment.Id, now, cancellationToken);

        return new PaymentDto(payment.Id, payment.PlanName, payment.AmountCents, payment.CurrencyCode, payment.CurrencySymbol, payment.LocalAmount, payment.Provider, payment.PaidAtUtc, payment.Kind.ToString());
    }
}
