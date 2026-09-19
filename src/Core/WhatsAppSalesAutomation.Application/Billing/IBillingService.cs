namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// Tenant-facing billing operations - implemented in Infrastructure (BillingService). Real-money
/// integration (Stripe) has been pulled out for this platform's India-first launch, since Stripe
/// doesn't work well for India (RBI's recurring-payment/export rules); Razorpay is the plan, but
/// there's no Razorpay integration yet either. Until then, <see cref="ChoosePlanAsync"/> simulates a
/// successful payment directly - see its own doc comment - rather than redirecting anywhere.
/// </summary>
public interface IBillingService
{
    /// <summary>The public plan catalog - every active Plan, safe to call unauthenticated. Each
    /// plan's local price is resolved from <paramref name="countryCode"/> when given (the anonymous/
    /// pre-signup preview case - see BillingController.GetPlans), else from the calling tenant's own
    /// stored Tenant.CountryCode when authenticated, else USD - see RegionalPricingCatalog.Resolve.</summary>
    Task<IReadOnlyList<PlanDto>> GetPlansAsync(string? countryCode = null, CancellationToken cancellationToken = default);

    /// <summary>The public region catalog - safe to call unauthenticated, backs the signup page's
    /// country picker.</summary>
    Task<IReadOnlyList<RegionDto>> GetRegionsAsync(CancellationToken cancellationToken = default);

    /// <summary>The calling tenant's current billing state - null if it has no Subscription row at
    /// all yet (see SubscriptionDto's own doc comment).</summary>
    Task<SubscriptionDto?> GetSubscriptionForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Simulates a successful payment for one plan and switches the tenant to it
    /// immediately - upserts the tenant's Subscription (PlanId, Status = Active, a fresh one-month
    /// CurrentPeriodStartUtc/EndUtc) and records one Payment row with that amount snapshotted in the
    /// tenant's own currency. Placeholder for a real payment gateway (Razorpay) - see
    /// IBillingService's own doc comment - not itself a Stripe-style redirect, so there's no
    /// success/cancel URL to pass; it either succeeds synchronously or throws.</summary>
    Task<SubscriptionDto> ChoosePlanAsync(Guid tenantId, Guid planId, CancellationToken cancellationToken = default);

    /// <summary>The calling tenant's payment history, most recent first - empty, never null, for a
    /// tenant that has never chosen a plan.</summary>
    Task<IReadOnlyList<PaymentDto>> GetPaymentHistoryForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Active credit packs, priced in the calling tenant's own currency.</summary>
    Task<IReadOnlyList<CreditPackDto>> GetCreditPacksAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Simulates paying for one credit pack and grants its units (valid 12 months). Allowed at any
    /// time the tenant has a plan that is not suspended or cancelled - before or after the included quota
    /// runs out. Throws ConflictException otherwise.</summary>
    Task<PaymentDto> PurchaseCreditPackAsync(Guid tenantId, Guid packId, CancellationToken cancellationToken = default);
}
