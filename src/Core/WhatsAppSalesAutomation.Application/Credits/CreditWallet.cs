using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

// The single credit wallet: what each action costs, which features a tenant's plan includes, the monthly cap a tenant puts on each feature, and the
// plan's credits arriving in the wallet. The ledger underneath is the existing one (QuotaLedgerService), with one wallet - QuotaType.Credits - and every
// spend tagged with its feature, so each feature has a ledger of its own.

// ----- what an action costs ---------------------------------------------------------------------------------------------------------------

/// <summary>What each action costs in credits, as the platform admin has set it (or the catalog default until then).</summary>
public interface ICreditCostService
{
    /// <summary>Credits one unit of the scenario costs. For product knowledge, credits per action.</summary>
    Task<decimal> GetCreditsPerUnitAsync(CreditScenario scenario, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<CreditScenario, decimal>> GetAllAsync(CancellationToken cancellationToken = default);
}

public class CreditCostService : ICreditCostService
{
    private readonly IApplicationDbContext _context;
    private Dictionary<CreditScenario, decimal>? _cache;

    public CreditCostService(IApplicationDbContext context) => _context = context;

    public async Task<decimal> GetCreditsPerUnitAsync(CreditScenario scenario, CancellationToken cancellationToken = default) =>
        (await GetAllAsync(cancellationToken))[scenario];

    public async Task<IReadOnlyDictionary<CreditScenario, decimal>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
            return _cache;

        var rows = await _context.CreditScenarioSettings.AsNoTracking().ToListAsync(cancellationToken);
        _cache = CreditScenarioCatalog.All.ToDictionary(info => info.Scenario, info =>
        {
            var row = rows.FirstOrDefault(r => r.Scenario == info.Scenario);
            // Product knowledge is priced per action (UnitsPerAction); everything else per unit its code sizes (CreditsPerUnit).
            var set = info.UnitsConfigurable ? row?.UnitsPerAction : row?.CreditsPerUnit;
            return set ?? info.DefaultUnits;
        });
        return _cache;
    }
}

// ----- which features a plan includes -----------------------------------------------------------------------------------------------------

/// <summary>Which features a tenant can use: the ones its plan includes. A feature outside the plan is hidden in the app and refused by the API.</summary>
public interface IFeatureAccessService
{
    Task<IReadOnlySet<PlatformFeature>> GetFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<bool> HasAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="FeatureDisabledException"/> (HTTP 403) when the tenant's plan does not include the feature.</summary>
    Task RequireAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default);
}

public class FeatureAccessService : IFeatureAccessService
{
    private readonly IApplicationDbContext _context;
    private readonly Dictionary<Guid, IReadOnlySet<PlatformFeature>> _cache = new();

    public FeatureAccessService(IApplicationDbContext context) => _context = context;

    public async Task<IReadOnlySet<PlatformFeature>> GetFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(tenantId, out var cached))
            return cached;

        // A tenant with no plan row yet is on the Trial plan - the minimum - not on nothing.
        var planId = await _context.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId).Select(s => s.PlanId).FirstOrDefaultAsync(cancellationToken);
        planId ??= await _context.Plans.AsNoTracking().Where(p => p.Code == PlatformFeatureCatalog.TrialPlanCode).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);

        var features = planId is null
            ? new HashSet<PlatformFeature>()
            : (await _context.PlanFeatures.AsNoTracking().Where(f => f.PlanId == planId).Select(f => f.Feature).ToListAsync(cancellationToken)).ToHashSet();

        return _cache[tenantId] = features;
    }

    public async Task<bool> HasAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default) =>
        (await GetFeaturesAsync(tenantId, cancellationToken)).Contains(feature);

    public async Task RequireAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default)
    {
        if (!await HasAsync(tenantId, feature, cancellationToken))
            throw new FeatureDisabledException($"{PlatformFeatureCatalog.For(feature).Label} is not part of your plan. Upgrade to Premium to use it.");
    }
}

// ----- the tenant's wallet, caps and per-feature ledgers ----------------------------------------------------------------------------------

/// <param name="Description">What the action is, in the platform's words, so the tenant's credit-costs page can say what a charge is for.</param>
public record FeatureCostDto(CreditScenario Scenario, string Title, string UnitLabel, decimal Credits, bool PerAction, string Description);

/// <param name="IncludedInPlan">False for a feature the tenant's plan does not include: the app hides it (the summary still lists it so a plan can be compared).</param>
/// <param name="MonthlyCap">The tenant's own limit on this feature's credits per calendar month, or null for none.</param>
public record FeatureWalletDto(
    PlatformFeature Feature,
    string Label,
    string Description,
    bool IncludedInPlan,
    decimal SpentThisMonth,
    decimal? MonthlyCap,
    IReadOnlyList<FeatureCostDto> Costs);

public record WalletSummaryDto(
    decimal Balance,
    string? PlanCode,
    string? PlanName,
    decimal MonthlyCredits,
    DateTime MonthStartUtc,
    IReadOnlyList<FeatureWalletDto> Features);

public record SetFeatureCapRequest(PlatformFeature Feature, decimal? MonthlyLimit);

/// <summary>What an action costs, for the placeholder every feature shows ("Uses 2 credits per reply").</summary>
public record CreditCostItemDto(PlatformFeature Feature, CreditScenario Scenario, string Title, string UnitLabel, decimal Credits, bool PerAction);

/// <summary>What the signed-in tenant is allowed to see and use: the features of its plan, its balance and what each action costs. Light enough to load for every user.</summary>
public record EntitlementsDto(string? PlanCode, string? PlanName, IReadOnlyList<PlatformFeature> Features, decimal Balance, IReadOnlyList<CreditCostItemDto> Costs);

public interface ICreditWalletService
{
    Task<EntitlementsDto> GetEntitlementsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<WalletSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Sets (or, with null, removes) the tenant's monthly limit for one feature. Returns the new summary.</summary>
    Task<WalletSummaryDto> SetCapAsync(Guid tenantId, SetFeatureCapRequest request, CancellationToken cancellationToken = default);

    /// <summary>The feature's own ledger: every spend and give-back for it, newest first.</summary>
    Task<PagedResult<QuotaLedgerEntryDto>> GetFeatureLedgerAsync(Guid tenantId, PlatformFeature feature, PagedRequest paging, CancellationToken cancellationToken = default);

    /// <summary>How many units of the scenario's action the tenant could still afford: its credits and the feature's monthly cap, divided by what one unit costs - and none at all when
    /// the feature is not in its plan.</summary>
    Task<decimal> GetAffordableUnitsAsync(Guid tenantId, CreditScenario scenario, CancellationToken cancellationToken = default);

    /// <summary>What the feature may still spend this month under the tenant's cap, or null when it has none.</summary>
    Task<decimal?> GetCapRemainingAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default);
}

public class CreditWalletService : ICreditWalletService
{
    private readonly IApplicationDbContext _context;
    private readonly IQuotaLedgerService _ledger;
    private readonly IFeatureAccessService _access;
    private readonly ICreditCostService _costs;
    private readonly IDateTimeProvider _dateTime;

    public CreditWalletService(IApplicationDbContext context, QuotaLedgerService ledger, IFeatureAccessService access, ICreditCostService costs, IDateTimeProvider dateTime)
    {
        _context = context;
        _ledger = ledger;
        _access = access;
        _costs = costs;
        _dateTime = dateTime;
    }

    public async Task<EntitlementsDto> GetEntitlementsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var summary = await GetSummaryAsync(tenantId, ct);
        var included = summary.Features.Where(f => f.IncludedInPlan).ToList();
        var costs = included.SelectMany(f => f.Costs.Select(c => new CreditCostItemDto(f.Feature, c.Scenario, c.Title, c.UnitLabel, c.Credits, c.PerAction))).ToList();
        return new EntitlementsDto(summary.PlanCode, summary.PlanName, included.Select(f => f.Feature).ToList(), summary.Balance, costs);
    }

    public async Task<WalletSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken ct = default)
    {
        var monthStart = MonthStart(_dateTime.UtcNow);
        var balance = (await _ledger.GetBalancesAsync(tenantId, ct)).Single(b => b.QuotaType == QuotaType.Credits).Balance;

        var planId = await _context.Subscriptions.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => s.PlanId).FirstOrDefaultAsync(ct);
        var plan = planId is null
            ? await _context.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == PlatformFeatureCatalog.TrialPlanCode, ct)
            : await _context.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);

        var included = await _access.GetFeaturesAsync(tenantId, ct);
        var costs = await _costs.GetAllAsync(ct);
        var spent = await SpentThisMonthAsync(tenantId, monthStart, ct);
        var caps = await _context.TenantFeatureCaps.IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId).ToDictionaryAsync(c => c.Feature, c => c.MonthlyLimit, ct);

        var features = PlatformFeatureCatalog.All.Select(f => new FeatureWalletDto(
            f.Feature, f.Label, f.Description, included.Contains(f.Feature),
            spent.GetValueOrDefault(f.Feature), caps.TryGetValue(f.Feature, out var cap) ? cap : null,
            f.Scenarios.Select(s =>
            {
                var info = CreditScenarioCatalog.For(s);
                return new FeatureCostDto(s, info.Title, info.UnitLabel, costs[s], info.UnitsConfigurable, info.Description);
            }).ToList())).ToList();

        return new WalletSummaryDto(balance, plan?.Code, plan?.Name, plan?.MonthlyCredits ?? 0m, monthStart, features);
    }

    public async Task<WalletSummaryDto> SetCapAsync(Guid tenantId, SetFeatureCapRequest request, CancellationToken ct = default)
    {
        if (request.MonthlyLimit is < 0)
            throw new FluentValidation.ValidationException(new[] { new FluentValidation.Results.ValidationFailure(nameof(request.MonthlyLimit), "A limit cannot be negative.") });
        await _access.RequireAsync(tenantId, request.Feature, ct);

        var existing = await _context.TenantFeatureCaps.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Feature == request.Feature, ct);
        if (request.MonthlyLimit is null)
        {
            if (existing is not null)
                _context.TenantFeatureCaps.Remove(existing);
        }
        else if (existing is null)
        {
            _context.TenantFeatureCaps.Add(new TenantFeatureCap { TenantId = tenantId, Feature = request.Feature, MonthlyLimit = request.MonthlyLimit.Value });
        }
        else
        {
            existing.MonthlyLimit = request.MonthlyLimit.Value;
        }

        await _context.SaveChangesAsync(ct);
        return await GetSummaryAsync(tenantId, ct);
    }

    public Task<PagedResult<QuotaLedgerEntryDto>> GetFeatureLedgerAsync(Guid tenantId, PlatformFeature feature, PagedRequest paging, CancellationToken ct = default) =>
        _ledger.GetLedgerAsync(tenantId, new QuotaLedgerQuery { QuotaType = QuotaType.Credits, Feature = feature, Page = paging.Page, PageSize = paging.PageSize }, ct);

    public async Task<decimal> GetAffordableUnitsAsync(Guid tenantId, CreditScenario scenario, CancellationToken ct = default)
    {
        var feature = PlatformFeatureCatalog.FeatureOf(scenario);
        if (!await _access.HasAsync(tenantId, feature, ct))
            return 0m;

        var balance = (await _ledger.GetBalancesAsync(tenantId, ct)).Single(b => b.QuotaType == QuotaType.Credits).Balance;
        var spendable = await GetCapRemainingAsync(tenantId, feature, ct) is { } remaining ? Math.Min(balance, remaining) : balance;
        var perUnit = await _costs.GetCreditsPerUnitAsync(scenario, ct);
        return perUnit <= 0 ? spendable : Math.Floor(spendable / perUnit);
    }

    public async Task<decimal?> GetCapRemainingAsync(Guid tenantId, PlatformFeature feature, CancellationToken ct = default)
    {
        var cap = await _context.TenantFeatureCaps.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Feature == feature).Select(c => (decimal?)c.MonthlyLimit).FirstOrDefaultAsync(ct);
        if (cap is null)
            return null;

        var spent = (await SpentThisMonthAsync(tenantId, MonthStart(_dateTime.UtcNow), ct)).GetValueOrDefault(feature);
        return Math.Max(0m, cap.Value - spent);
    }

    /// <summary>Credits each feature has spent since the month began: spends less what was given back.</summary>
    private async Task<Dictionary<PlatformFeature, decimal>> SpentThisMonthAsync(Guid tenantId, DateTime monthStart, CancellationToken ct)
    {
        var rows = await _context.QuotaLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Feature != null && e.OccurredAtUtc >= monthStart
                        && (e.EntryType == QuotaEntryType.Consumption || e.EntryType == QuotaEntryType.ConsumptionReversal))
            .GroupBy(e => e.Feature!.Value)
            .Select(g => new { Feature = g.Key, Delta = g.Sum(e => e.UnitsDelta) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Feature, r => Math.Max(0m, -r.Delta));
    }

    public static DateTime MonthStart(DateTime nowUtc) => new(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}

// ----- credits arriving in the wallet -----------------------------------------------------------------------------------------------------

/// <summary>Puts a plan's credits into a tenant's wallet: the starter credits once when the tenant goes onto the plan, and the monthly credits each month.
/// The plan never expires and unspent credits carry over, so nothing here takes credits away.</summary>
public interface ICreditAllocationService
{
    /// <summary>Moves a tenant onto a plan (creating its subscription if it has none) and grants the plan's starter credits and this month's credits.</summary>
    /// <param name="paymentId">The payment that bought the plan, when one did: this month's credits are tied to it so the payment can be refunded.</param>
    Task AssignPlanAsync(Guid tenantId, Guid planId, Guid? paymentId = null, CancellationToken cancellationToken = default);

    /// <summary>Puts a brand-new tenant on the Trial plan. A platform with no Trial plan yet leaves the tenant as it is.</summary>
    Task AssignTrialPlanAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Gives every tenant on a plan with monthly credits this month's allotment. Safe to run as often as you like: a month is granted once.</summary>
    Task<int> AllocateMonthlyAsync(CancellationToken cancellationToken = default);
}

public class CreditAllocationService : ICreditAllocationService
{
    private readonly IApplicationDbContext _context;
    private readonly IQuotaLedgerService _ledger;
    private readonly IDateTimeProvider _dateTime;

    public CreditAllocationService(IApplicationDbContext context, IQuotaLedgerService ledger, IDateTimeProvider dateTime)
    {
        _context = context;
        _ledger = ledger;
        _dateTime = dateTime;
    }

    public async Task AssignPlanAsync(Guid tenantId, Guid planId, Guid? paymentId = null, CancellationToken ct = default)
    {
        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct) ?? throw new NotFoundException(nameof(Plan), planId);

        var subscription = await _context.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (subscription is null)
        {
            subscription = new Subscription { TenantId = tenantId };
            _context.Subscriptions.Add(subscription);
        }

        // The plan never expires: no period, no renewal. Active means "on this plan".
        subscription.PlanId = plan.Id;
        subscription.Status = SubscriptionStatus.Active;
        subscription.CurrentPeriodStartUtc = null;
        subscription.CurrentPeriodEndUtc = null;
        await _context.SaveChangesAsync(ct);

        // Once per plan the tenant has been on: going back and forth never hands out the starter credits twice.
        await _ledger.GrantPlanCreditsAsync(tenantId, plan.StarterCredits, $"starter:{plan.Id:N}", $"{plan.Name} plan starter credits", QuotaGrantOrigin.Trial, null, ct);
        await GrantMonthAsync(tenantId, plan, _dateTime.UtcNow, ct, paymentId);
    }

    public async Task AssignTrialPlanAsync(Guid tenantId, CancellationToken ct = default)
    {
        var trialId = await _context.Plans.AsNoTracking().Where(p => p.Code == PlatformFeatureCatalog.TrialPlanCode).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (trialId is { } id)
            await AssignPlanAsync(tenantId, id, null, ct);
    }

    public async Task<int> AllocateMonthlyAsync(CancellationToken ct = default)
    {
        var now = _dateTime.UtcNow;
        var due = await (
                from s in _context.Subscriptions.IgnoreQueryFilters().AsNoTracking()
                join p in _context.Plans.AsNoTracking() on s.PlanId equals p.Id
                where p.MonthlyCredits > 0 && s.Status == SubscriptionStatus.Active
                select new { s.TenantId, Plan = p })
            .ToListAsync(ct);

        var granted = 0;
        foreach (var item in due)
        {
            if (await GrantMonthAsync(item.TenantId, item.Plan, now, ct))
                granted++;
        }

        return granted;
    }

    private Task<bool> GrantMonthAsync(Guid tenantId, Plan plan, DateTime nowUtc, CancellationToken ct, Guid? paymentId = null) =>
        plan.MonthlyCredits <= 0
            ? Task.FromResult(false)
            : _ledger.GrantPlanCreditsAsync(tenantId, plan.MonthlyCredits, $"monthly:{nowUtc:yyyyMM}", $"{plan.Name} plan credits for {nowUtc:MMMM yyyy}", QuotaGrantOrigin.PlanAllocation, paymentId, ct);
}
