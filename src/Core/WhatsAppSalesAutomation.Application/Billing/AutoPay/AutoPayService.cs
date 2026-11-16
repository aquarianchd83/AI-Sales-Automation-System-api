using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>
/// Recurring plan billing through Razorpay UPI AutoPay: starting a mandate, reading where it stands, and cancelling it.
/// Razorpay debits the tenant each month by itself once the mandate is authorised - nothing here ever initiates a debit.
/// </summary>
public interface IAutoPayService
{
    /// <summary>Whether a plan priced in <paramref name="currencyCode"/> must be paid for through AutoPay: it is switched on, Razorpay is
    /// set up, and the currency is one UPI can debit. When true, the simulated "choose plan" path refuses a paid plan.</summary>
    Task<bool> IsRequiredAsync(string currencyCode, CancellationToken cancellationToken = default);

    Task<AutoPayAvailabilityDto> GetAvailabilityAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Creates the mandate at Razorpay for one plan and returns where the tenant authorises it. Asking again while one is still
    /// waiting returns that same one. Throws <see cref="ConflictException"/> when the tenant cannot subscribe this way.</summary>
    Task<AutoPayCheckoutDto> CheckoutAsync(Guid tenantId, Guid planId, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>The tenant's mandate that matters now - the one still alive, else the most recent. Null when it never started one.</summary>
    Task<AutoPaySubscriptionDto?> GetCurrentAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="NotFoundException"/> for an id that is not this tenant's.</summary>
    Task<AutoPaySubscriptionDto> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Asks Razorpay where the mandate stands and records it. What the screen calls when the tenant comes back from authorising:
    /// the answer is Razorpay's, not the browser's.</summary>
    Task<AutoPaySubscriptionDto> RefreshAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Cancels the mandate at Razorpay. Access already paid for is kept until its period ends. Nothing changes here unless
    /// Razorpay accepted the cancellation.</summary>
    Task<AutoPaySubscriptionDto> CancelAsync(Guid tenantId, Guid id, bool cancelAtCycleEnd, CancellationToken cancellationToken = default);
}

public class AutoPayService : IAutoPayService
{
    public const string EnabledKey = AutoPaySettings.EnabledKey;

    /// <summary>UPI AutoPay debits rupees only.</summary>
    public const string Currency = "INR";

    public const string Period = "monthly";
    public const int Interval = 1;

    private static readonly RazorpaySubscriptionStatus[] Running =
    {
        RazorpaySubscriptionStatus.Authenticated, RazorpaySubscriptionStatus.Active, RazorpaySubscriptionStatus.Pending,
        RazorpaySubscriptionStatus.Halted, RazorpaySubscriptionStatus.Paused
    };

    private static IReadOnlyList<RazorpaySubscriptionStatus> Ended => RazorpayMandateState.Ended;

    private readonly IApplicationDbContext _context;
    private readonly IAppSettingsStore _store;
    private readonly IRazorpayGateway _gateway;
    private readonly IPricingService _pricing;
    private readonly IDateTimeProvider _clock;
    private readonly AutoPayOptions _options;
    private readonly ILogger<AutoPayService> _logger;

    public AutoPayService(
        IApplicationDbContext context,
        IAppSettingsStore store,
        IRazorpayGateway gateway,
        IPricingService pricing,
        IDateTimeProvider clock,
        IOptionsSnapshot<AutoPayOptions> options,
        ILogger<AutoPayService> logger)
    {
        _context = context;
        _store = store;
        _gateway = gateway;
        _pricing = pricing;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    // ── Availability ────────────────────────────────────────────────────────────────────

    public async Task<bool> IsRequiredAsync(string currencyCode, CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        return settings.Enabled && settings.Credentials is not null && string.Equals(currencyCode, Currency, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AutoPayAvailabilityDto> GetAvailabilityAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        var mode = settings.Credentials?.Mode;
        if (!settings.Enabled)
            return new AutoPayAvailabilityDto(false, "Automatic payments are not switched on.", mode);
        if (settings.Credentials is null)
            return new AutoPayAvailabilityDto(false, "Razorpay is not set up.", mode);

        var countryCode = await _context.Tenants.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == tenantId).Select(t => t.CountryCode).FirstOrDefaultAsync(cancellationToken);
        return RegionalPricingCatalog.Resolve(countryCode).CurrencyCode == Currency
            ? new AutoPayAvailabilityDto(true, null, mode)
            : new AutoPayAvailabilityDto(false, "UPI AutoPay is available only for plans billed in Indian rupees.", mode);
    }

    // ── Checkout ────────────────────────────────────────────────────────────────────────

    public async Task<AutoPayCheckoutDto> CheckoutAsync(Guid tenantId, Guid planId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        if (!settings.Enabled)
            throw new ConflictException("Automatic payments are not switched on.");
        var credentials = settings.Credentials ?? throw new ConflictException("Razorpay is not set up, so a subscription cannot be started.");

        var plan = await _context.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), planId);
        var tenant = await _context.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

        var amountMinor = QuoteAmountMinor(plan, tenant, await LoadPlanPriceAsync(plan.Id, tenant.CountryCode, cancellationToken));

        var open = await _context.RazorpaySubscriptions.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && !Ended.Contains(s.Status))
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        // Mandates made with the other key set (test ones, once the platform goes live) do not exist for these keys: they cannot be
        // checked or cancelled with them, and must not stand in the way. One that was never authorised is closed here.
        foreach (var foreign in open.Where(s => s.Mode != credentials.Mode && s.Status == RazorpaySubscriptionStatus.Created))
            MarkEnded(foreign, RazorpaySubscriptionStatus.Expired);
        var hadOpen = open.Count > 0;
        open = open.Where(s => s.Mode == credentials.Mode).ToList();

        foreach (var existing in open)
        {
            // Razorpay's word on it first: it may have been authorised, cancelled or expired since we last heard.
            var fetched = await _gateway.FetchSubscriptionAsync(credentials, existing.RazorpaySubscriptionId, cancellationToken);
            if (!fetched.Success || fetched.Value is null)
                throw new ConflictException($"Razorpay could not be reached to check your existing subscription: {fetched.Error}");
            Apply(existing, fetched.Value);
        }
        if (hadOpen)
            await _context.SaveChangesAsync(cancellationToken);

        if (open.Any(s => Running.Contains(s.Status)))
            throw new ConflictException("You already have an automatic payment set up. Cancel it before starting another.");

        var waiting = open.FirstOrDefault(s => s.Status == RazorpaySubscriptionStatus.Created);
        if (waiting is not null)
        {
            // The same offer, still open: hand back the same authorisation rather than a second mandate to approve.
            if (waiting.PlanId == plan.Id && waiting.AmountMinor == amountMinor && waiting.Mode == credentials.Mode && !string.IsNullOrEmpty(waiting.ShortUrl))
                return ToCheckout(waiting, plan.Name, credentials);

            // A different plan, price or key set: the old one must be gone at Razorpay before another is offered, or both could be approved.
            var cancelled = await _gateway.CancelSubscriptionAsync(credentials, waiting.RazorpaySubscriptionId, false, cancellationToken);
            if (!cancelled.Success || cancelled.Value is null)
                throw new ConflictException($"An earlier subscription request is still open and Razorpay could not cancel it: {cancelled.Error}");
            Apply(waiting, cancelled.Value);
            MarkEnded(waiting, RazorpaySubscriptionStatus.Cancelled);
            await _context.SaveChangesAsync(cancellationToken);
        }

        var razorpayPlan = await FindOrCreatePlanAsync(credentials, plan, amountMinor, cancellationToken);

        long? expireBy = _options.AuthorizationExpiryHours > 0
            ? new DateTimeOffset(DateTime.SpecifyKind(_clock.UtcNow, DateTimeKind.Utc).AddHours(_options.AuthorizationExpiryHours)).ToUnixTimeSeconds()
            : null;
        var notes = new Dictionary<string, string>
        {
            ["source"] = "autopay",
            ["tenant_id"] = tenantId.ToString("N"),
            ["plan_code"] = plan.Code
        };

        // customer_notify false: the tenant is taken to the authorisation page by this app, and hears from this app afterwards.
        var created = await _gateway.CreateSubscriptionAsync(
            credentials, razorpayPlan.RazorpayPlanId, Math.Max(1, _options.TotalCount), false, expireBy, notes, cancellationToken);
        if (!created.Success || created.Value is null)
            throw new ConflictException($"Razorpay could not start the subscription: {created.Error}");
        if (string.IsNullOrEmpty(created.Value.ShortUrl))
            throw new ConflictException("Razorpay started the subscription but gave no page to authorise it on.");

        var row = new RazorpaySubscription
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            RazorpayPlanRowId = razorpayPlan.Id,
            RazorpaySubscriptionId = created.Value.Id,
            Mode = credentials.Mode,
            AmountMinor = amountMinor,
            Currency = Currency,
            CreatedByUserId = userId
        };
        Apply(row, created.Value);
        _context.RazorpaySubscriptions.Add(row);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Another checkout for this tenant got there first (the database allows one waiting authorisation). Ours must not stay open at Razorpay.
            _context.ResetChangeTracker();
            var undone = await _gateway.CancelSubscriptionAsync(credentials, created.Value.Id, false, cancellationToken);
            _logger.LogWarning(ex,
                "AutoPay checkout for tenant {TenantId} lost a race; Razorpay subscription {SubscriptionId} cancelled: {Cancelled} {Error}",
                tenantId, created.Value.Id, undone.Success, undone.Error);
            throw new ConflictException("A subscription request is already in progress. Refresh the page and continue with that one.");
        }

        _logger.LogInformation(
            "AutoPay mandate {SubscriptionId} created for tenant {TenantId}, plan {PlanCode}, {Amount} ({Mode})",
            row.RazorpaySubscriptionId, tenantId, plan.Code, RazorpayService.Format(amountMinor, Currency), row.Mode);

        return ToCheckout(row, plan.Name, credentials);
    }

    /// <summary>What the tenant is debited each month, in paise: the price set for their country plus tax, worked out here from the
    /// database - the same quote the plan list shows. Refuses anything UPI AutoPay cannot carry.</summary>
    private long QuoteAmountMinor(Plan plan, Tenant tenant, decimal? planPrice)
    {
        var quote = _pricing.Quote(planPrice, tenant.CountryCode, tenant.StateCode, allowFree: true)
            ?? throw new ConflictException("This plan isn't available in your country.");
        if (quote.Total <= 0)
            throw new ConflictException("This plan is free, so there is nothing to pay automatically.");
        if (!string.Equals(quote.CurrencyCode, Currency, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("UPI AutoPay is available only for plans billed in Indian rupees.");
        if (quote.Total > _options.MaxRecurringAmountInr)
            throw new ConflictException(
                $"{plan.Name} costs {quote.CurrencySymbol}{quote.Total:#,##0.00} a month, which is more than UPI AutoPay allows for one payment ({quote.CurrencySymbol}{_options.MaxRecurringAmountInr:#,##0}).");

        var minor = quote.Total * 100m;
        if (decimal.Truncate(minor) != minor)
            throw new ConflictException("This plan's price does not come to a whole number of paise, so it cannot be charged.");
        return (long)minor;
    }

    private async Task<decimal?> LoadPlanPriceAsync(Guid planId, string? countryCode, CancellationToken cancellationToken)
    {
        var region = RegionalPricingCatalog.Resolve(countryCode);
        var prices = await CatalogPricing.LoadPlanPricesAsync(_context, new[] { planId }, cancellationToken);
        return CatalogPricing.PriceFor(prices.GetValueOrDefault(planId), region.CountryCode);
    }

    /// <summary>The Razorpay plan for this exact charge: the one already made for it, else a new one. Razorpay plans cannot be edited, so
    /// each distinct amount has its own and is reused by every tenant who pays that amount.</summary>
    private async Task<RazorpayPlan> FindOrCreatePlanAsync(RazorpayCredentials credentials, Plan plan, long amountMinor, CancellationToken cancellationToken)
    {
        Task<RazorpayPlan?> FindAsync() => _context.RazorpayPlans.AsNoTracking().FirstOrDefaultAsync(
            p => p.PlanId == plan.Id && p.Mode == credentials.Mode && p.Currency == Currency && p.AmountMinor == amountMinor && p.Period == Period && p.Interval == Interval,
            cancellationToken);

        if (await FindAsync() is { } known)
            return known;

        var notes = new Dictionary<string, string> { ["source"] = "autopay", ["plan_code"] = plan.Code };
        var created = await _gateway.CreatePlanAsync(credentials, Period, Interval, $"{plan.Name} ({Period})", amountMinor, Currency, notes, cancellationToken);
        if (!created.Success || created.Value is null)
            throw new ConflictException($"Razorpay could not set up the plan: {created.Error}");
        if (created.Value.Amount != amountMinor || !string.Equals(created.Value.Currency, Currency, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("Razorpay set the plan up with a different amount than was asked for, so it was not used.");

        var row = new RazorpayPlan
        {
            PlanId = plan.Id,
            Mode = credentials.Mode,
            Currency = Currency,
            AmountMinor = amountMinor,
            Period = Period,
            Interval = Interval,
            RazorpayPlanId = created.Value.Id
        };
        _context.RazorpayPlans.Add(row);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return row;
        }
        catch (DbUpdateException)
        {
            // Two tenants subscribed at the same moment and the other one's plan was saved first. Use theirs; ours is left unused at
            // Razorpay, which costs nothing.
            _context.ResetChangeTracker();
            return await FindAsync() ?? throw new ConflictException("The plan could not be set up. Please try again.");
        }
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────

    public async Task<AutoPaySubscriptionDto?> GetCurrentAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var rows = await _context.RazorpaySubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        // One that is running outranks one still waiting to be authorised, which outranks anything finished.
        var row = rows.FirstOrDefault(s => Running.Contains(s.Status))
            ?? rows.FirstOrDefault(s => s.Status == RazorpaySubscriptionStatus.Created)
            ?? rows.FirstOrDefault();
        return row is null ? null : await ToDtoAsync(row, cancellationToken);
    }

    public async Task<AutoPaySubscriptionDto> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default) =>
        await ToDtoAsync(await FindAsync(tenantId, id, cancellationToken), cancellationToken);

    public async Task<AutoPaySubscriptionDto> RefreshAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(tenantId, id, cancellationToken);

        // Finished for good: Razorpay never brings a cancelled, completed or expired subscription back.
        if (Ended.Contains(row.Status))
            return await ToDtoAsync(row, cancellationToken);

        var credentials = (await ReadSettingsAsync(cancellationToken)).Credentials
            ?? throw new ConflictException("Razorpay is not set up, so the subscription cannot be checked.");
        var fetched = await _gateway.FetchSubscriptionAsync(credentials, row.RazorpaySubscriptionId, cancellationToken);
        if (!fetched.Success || fetched.Value is null)
            throw new ConflictException($"Razorpay could not be reached to check the subscription: {fetched.Error}");

        Apply(row, fetched.Value);
        await _context.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(row, cancellationToken);
    }

    // ── Cancelling ──────────────────────────────────────────────────────────────────────

    public async Task<AutoPaySubscriptionDto> CancelAsync(Guid tenantId, Guid id, bool cancelAtCycleEnd, CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(tenantId, id, cancellationToken);

        // Already over, or already set to stop: asking again changes nothing.
        if (Ended.Contains(row.Status) || (cancelAtCycleEnd && row.CancelAtCycleEnd))
            return await ToDtoAsync(row, cancellationToken);

        var credentials = (await ReadSettingsAsync(cancellationToken)).Credentials
            ?? throw new ConflictException("Razorpay is not set up, so the subscription cannot be cancelled.");

        // "At the end of the cycle" needs a paid cycle in progress. One never authorised, or one whose payments have stopped, has none.
        var atCycleEnd = cancelAtCycleEnd && row.Status is not (RazorpaySubscriptionStatus.Created or RazorpaySubscriptionStatus.Halted);

        var result = await _gateway.CancelSubscriptionAsync(credentials, row.RazorpaySubscriptionId, atCycleEnd, cancellationToken);
        if (!result.Success || result.Value is null)
        {
            _logger.LogWarning("AutoPay cancel of {SubscriptionId} for tenant {TenantId} was refused: {Error}", row.RazorpaySubscriptionId, tenantId, result.Error);
            throw new ConflictException($"Razorpay could not cancel the subscription: {result.Error}");
        }

        Apply(row, result.Value);
        if (atCycleEnd)
            row.CancelAtCycleEnd = true;
        else
            MarkEnded(row, RazorpaySubscriptionStatus.Cancelled);

        // The plan stays: what has been paid for runs to the end of its period. This only records that it will not renew.
        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.RazorpaySubscriptionRowId == row.Id, cancellationToken);
        if (subscription is not null)
            subscription.CancelAtPeriodEnd = true;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "AutoPay mandate {SubscriptionId} for tenant {TenantId} cancelled ({When})",
            row.RazorpaySubscriptionId, tenantId, atCycleEnd ? "at cycle end" : "immediately");

        return await ToDtoAsync(row, cancellationToken);
    }

    // ── Razorpay's state onto ours ──────────────────────────────────────────────────────

    private void Apply(RazorpaySubscription row, RazorpaySubscriptionInfo info)
    {
        if (!RazorpayMandateState.Apply(row, info, _clock.UtcNow))
            _logger.LogWarning("Razorpay subscription {SubscriptionId} reported an unknown status '{Status}'; kept {Current}", info.Id, info.Status, row.Status);
    }

    private void MarkEnded(RazorpaySubscription row, RazorpaySubscriptionStatus status) => RazorpayMandateState.MarkEnded(row, status, _clock.UtcNow);

    public static RazorpaySubscriptionStatus? ParseStatus(string? status) => RazorpayMandateState.ParseStatus(status);

    /// <summary>What the billing screen shows for a mandate. "Active" needs a debit Razorpay has counted: an authorised mandate with nothing
    /// paid yet is still waiting for its first payment.</summary>
    public static string DisplayStatus(RazorpaySubscription row) => row.Status switch
    {
        RazorpaySubscriptionStatus.Created => AutoPayDisplayStatus.PendingAuthorization,
        RazorpaySubscriptionStatus.Authenticated or RazorpaySubscriptionStatus.Active when row.CancelAtCycleEnd => AutoPayDisplayStatus.CancellationScheduled,
        RazorpaySubscriptionStatus.Authenticated => row.PaidCount > 0 ? AutoPayDisplayStatus.Active : AutoPayDisplayStatus.PaymentPending,
        RazorpaySubscriptionStatus.Active => AutoPayDisplayStatus.Active,
        RazorpaySubscriptionStatus.Pending => AutoPayDisplayStatus.PastDue,
        RazorpaySubscriptionStatus.Halted => AutoPayDisplayStatus.Halted,
        RazorpaySubscriptionStatus.Paused => AutoPayDisplayStatus.Paused,
        RazorpaySubscriptionStatus.Cancelled => AutoPayDisplayStatus.Cancelled,
        _ => AutoPayDisplayStatus.Expired
    };

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>Always by tenant as well as id: one tenant can never read or cancel another's mandate, whatever id it sends.</summary>
    private async Task<RazorpaySubscription> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await _context.RazorpaySubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(RazorpaySubscription), id);

    private async Task<AutoPaySubscriptionDto> ToDtoAsync(RazorpaySubscription row, CancellationToken cancellationToken)
    {
        var planName = await _context.Plans.AsNoTracking().Where(p => p.Id == row.PlanId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var renews = !row.CancelAtCycleEnd
                     && row.Status is RazorpaySubscriptionStatus.Authenticated or RazorpaySubscriptionStatus.Active or RazorpaySubscriptionStatus.Pending;

        return new AutoPaySubscriptionDto(
            row.Id, row.RazorpaySubscriptionId, row.PlanId, planName, DisplayStatus(row), row.Status.ToString(),
            row.AmountMinor / 100m, row.Currency, "Monthly",
            row.CurrentStartUtc, row.CurrentEndUtc,
            renews ? row.ChargeAtUtc ?? row.CurrentEndUtc : null,
            renews, row.CancelAtCycleEnd, row.CancelledAtUtc, row.PaymentMethod,
            row.Status == RazorpaySubscriptionStatus.Created ? row.ShortUrl : null,
            row.Mode, row.CreatedAt);
    }

    private static AutoPayCheckoutDto ToCheckout(RazorpaySubscription row, string planName, RazorpayCredentials credentials) => new(
        row.Id, row.RazorpaySubscriptionId, AutoPayDisplayStatus.PendingAuthorization, row.ShortUrl ?? string.Empty,
        credentials.KeyId, row.Mode, row.AmountMinor, row.Currency, planName);

    private Task<AutoPaySettings> ReadSettingsAsync(CancellationToken cancellationToken) => AutoPaySettings.ReadAsync(_store, cancellationToken);
}
