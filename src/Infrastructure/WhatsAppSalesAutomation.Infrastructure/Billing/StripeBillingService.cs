using Microsoft.EntityFrameworkCore;
using Stripe;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
// "Subscription" is ambiguous between the local entity and Stripe.Subscription (the Stripe SDK type,
// not used by name in this file but brought into scope by "using Stripe" above) - see
// StripeWebhookHandler's identical alias for the same reasoning.
using TenantSubscription = WhatsAppSalesAutomation.Domain.Entities.Billing.Subscription;
using Plan = WhatsAppSalesAutomation.Domain.Entities.Billing.Plan;

namespace WhatsAppSalesAutomation.Infrastructure.Billing;

/// <summary>Real implementation of <see cref="IBillingService"/> against Stripe's Checkout/Customer
/// Portal/Customer APIs. Takes a <see cref="StripeClient"/> (registered as a platform-wide singleton -
/// see DependencyInjection.AddBilling) rather than relying on Stripe.net's static
/// StripeConfiguration.ApiKey, so the secret key stays ordinary DI-injected config like everything
/// else in this codebase instead of a process-wide static.</summary>
public class StripeBillingService : IBillingService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly StripeClient _stripeClient;

    public StripeBillingService(IApplicationDbContext context, ITenantContext tenantContext, StripeClient stripeClient)
    {
        _context = context;
        _tenantContext = tenantContext;
        _stripeClient = stripeClient;
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

        return new SubscriptionDto(subscription.PlanId, planName, subscription.Status.ToString(), subscription.CurrentPeriodEndUtc);
    }

    public async Task<string> CreateCheckoutSessionAsync(
        Guid tenantId, Guid planId, string successUrl, string cancelUrl, CancellationToken cancellationToken = default)
    {
        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), planId);

        if (string.IsNullOrWhiteSpace(plan.StripePriceId))
            throw new InvalidOperationException($"Plan '{plan.Code}' has no Stripe price configured yet.");

        var stripeCustomerId = await EnsureStripeCustomerAsync(tenantId, cancellationToken);

        var options = new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            Customer = stripeCustomerId,
            LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
            {
                new() { Price = plan.StripePriceId, Quantity = 1 }
            },
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            // So StripeWebhookHandler can resolve exactly which tenant/plan a checkout.session.completed
            // event is for without depending solely on StripeCustomerId already being on file locally.
            Metadata = new Dictionary<string, string>
            {
                ["tenantId"] = tenantId.ToString(),
                ["planId"] = plan.Id.ToString()
            }
        };

        var sessionService = new Stripe.Checkout.SessionService(_stripeClient);
        var session = await sessionService.CreateAsync(options, cancellationToken: cancellationToken);
        return session.Url;
    }

    public async Task<string> CreateBillingPortalSessionAsync(Guid tenantId, string returnUrl, CancellationToken cancellationToken = default)
    {
        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        if (subscription?.StripeCustomerId is not { } stripeCustomerId)
            throw new InvalidOperationException("This tenant has no Stripe customer yet - complete Checkout first.");

        var options = new Stripe.BillingPortal.SessionCreateOptions { Customer = stripeCustomerId, ReturnUrl = returnUrl };
        var portalService = new Stripe.BillingPortal.SessionService(_stripeClient);
        var session = await portalService.CreateAsync(options, cancellationToken: cancellationToken);
        return session.Url;
    }

    /// <summary>Creates a Stripe Customer for this tenant the first time it's needed, and remembers
    /// the id on its Subscription row (creating one, Trialing, if none exists yet) - every later
    /// Checkout/Portal call for this tenant reuses it, so Stripe sees one Customer per tenant, not a
    /// new one per Checkout attempt.</summary>
    private async Task<string> EnsureStripeCustomerAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        if (subscription?.StripeCustomerId is { } existingCustomerId)
            return existingCustomerId;

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

        var customerService = new Stripe.CustomerService(_stripeClient);
        var customer = await customerService.CreateAsync(new CustomerCreateOptions
        {
            Name = tenant.Name,
            Metadata = new Dictionary<string, string> { ["tenantId"] = tenantId.ToString() }
        }, cancellationToken: cancellationToken);

        if (subscription is null)
        {
            subscription = new TenantSubscription { TenantId = tenantId, StripeCustomerId = customer.Id, Status = SubscriptionStatus.Trialing };
            _context.Subscriptions.Add(subscription);
        }
        else
        {
            subscription.StripeCustomerId = customer.Id;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return customer.Id;
    }
}
