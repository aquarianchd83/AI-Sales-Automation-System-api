using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WhatsAppSalesAutomation.Application.Platform;
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

/// <summary>
/// One feature a tenant's plan does not include but gives free uses of: how many, how many are gone, and what one use is. Once
/// <see cref="Remaining"/> reaches zero the feature is locked like any other feature outside the plan, until the tenant upgrades.
/// </summary>
/// <param name="Unit">What one use is, plural ("leads", "AI generations"), for "3 of 10 free leads left".</param>
public record FeatureTrialDto(PlatformFeature Feature, string Label, int FreeUses, int Used, string Unit, string UnitSingular)
{
    public int Remaining => Math.Max(0, FreeUses - Used);

    public bool Exhausted => Remaining == 0;
}

/// <summary>
/// Which features a tenant can use: the ones its plan includes, plus any the plan gives free uses of while some are left. A feature outside
/// both is hidden in the app and refused by the API.
/// </summary>
public interface IFeatureAccessService
{
    Task<IReadOnlySet<PlatformFeature>> GetFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<bool> HasAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="FeatureDisabledException"/> (HTTP 403) when the tenant can't use the feature: not in its plan, and no free uses left.</summary>
    Task RequireAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default);

    /// <summary>The free uses the tenant's plan gives of each published feature it does not include (used up or not). Empty for a plan that gives none.</summary>
    Task<IReadOnlyList<FeatureTrialDto>> GetTrialsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The free uses of one feature, or null when the plan includes the feature (no counting needed) or gives no free uses of it.</summary>
    Task<FeatureTrialDto?> GetTrialAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default);

    /// <summary>Counts one free use of a feature outside the tenant's plan. Called once a use has really been charged.</summary>
    Task RecordTrialUseAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default);
}

public class FeatureAccessService : IFeatureAccessService
{
    private const int MaxAttempts = 3;

    private readonly IApplicationDbContext _context;
    private readonly IConfiguration? _configuration;
    private readonly FeatureAccessCache? _shared;
    private readonly IDateTimeProvider? _dateTime;
    private readonly Dictionary<Guid, PlanAccess> _cache = new();

    public FeatureAccessService(IApplicationDbContext context, IConfiguration? configuration = null, FeatureAccessCache? shared = null, IDateTimeProvider? dateTime = null)
    {
        _context = context;
        _configuration = configuration;
        _shared = shared;
        _dateTime = dateTime;
    }

    /// <summary>
    /// The tenant's plan: its features and the free uses it gives of the others. Held for the request, and process-wide in the shared cache. Only the
    /// plan is cached - what the tenant has used of its free uses is read fresh every time, so a lead discovery run that uses the last one stops at once.
    /// </summary>
    private async Task<PlanAccess> GetPlanAccessAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(tenantId, out var cached))
            return cached;

        if (_shared is not null && _shared.TryGet(tenantId, out var shared))
            return _cache[tenantId] = shared;

        var generation = _shared?.Generation ?? 0;

        // A tenant with no plan row yet is on the Trial plan - the minimum - not on nothing.
        var planId = await _context.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId).Select(s => s.PlanId).FirstOrDefaultAsync(cancellationToken);
        planId ??= await _context.Plans.AsNoTracking().Where(p => p.Code == PlatformFeatureCatalog.TrialPlanCode).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);

        PlanAccess access;
        if (planId is null)
        {
            access = PlanAccess.None;
        }
        else
        {
            var features = (await _context.PlanFeatures.AsNoTracking().Where(f => f.PlanId == planId).Select(f => f.Feature).ToListAsync(cancellationToken)).ToHashSet();
            var trials = await _context.PlanFeatureTrials.AsNoTracking().Where(t => t.PlanId == planId && t.FreeUses > 0)
                .Select(t => new { t.Feature, t.FreeUses }).ToListAsync(cancellationToken);
            var planName = await _context.Plans.AsNoTracking().Where(p => p.Id == planId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken);
            access = new PlanAccess(
                features,
                trials.Where(t => !features.Contains(t.Feature) && PlatformFeatureCatalog.TrialUnitFor(t.Feature) is not null).ToDictionary(t => t.Feature, t => t.FreeUses),
                planName);
        }

        _shared?.Set(tenantId, access, generation);
        return _cache[tenantId] = access;
    }

    private Task<Dictionary<PlatformFeature, int>> LoadUsedAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _context.TenantFeatureTrialUsages.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .ToDictionaryAsync(u => u.Feature, u => u.Used, cancellationToken);

    public async Task<IReadOnlySet<PlatformFeature>> GetFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var access = await GetPlanAccessAsync(tenantId, cancellationToken);
        var features = new HashSet<PlatformFeature>(access.Features);

        // A feature the plan only gives free uses of is open while some are left. The unpublished switches are applied fresh every
        // time, so a platform admin's switch is never held back by the cache.
        if (access.FreeUses.Count > 0)
        {
            var used = await LoadUsedAsync(tenantId, cancellationToken);
            features.UnionWith(access.FreeUses.Where(t => used.GetValueOrDefault(t.Key) < t.Value).Select(t => t.Key));
        }

        features.ExceptWith(PlatformFeatureSwitches.Unpublished(_configuration));
        return features;
    }

    public async Task<bool> HasAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default)
    {
        if (PlatformFeatureSwitches.Unpublished(_configuration).Contains(feature))
            return false;

        var access = await GetPlanAccessAsync(tenantId, cancellationToken);
        if (access.Features.Contains(feature))
            return true;

        return await GetTrialAsync(tenantId, feature, cancellationToken) is { Exhausted: false };
    }

    public async Task RequireAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default)
    {
        if (await HasAsync(tenantId, feature, cancellationToken))
            return;
        var label = PlatformFeatureCatalog.For(feature).Label;
        if (PlatformFeatureSwitches.Unpublished(_configuration).Contains(feature))
            throw new FeatureDisabledException($"{label} is not available right now.");

        if (await GetTrialAsync(tenantId, feature, cancellationToken) is { } trial)
            throw new FeatureDisabledException(TrialUsedUpMessage(trial, (await GetPlanAccessAsync(tenantId, cancellationToken)).PlanName));

        throw new FeatureDisabledException($"{label} is not part of your plan. Upgrade to Premium to use it.");
    }

    /// <summary>What a tenant is told once the free uses of a feature are gone.</summary>
    public static string TrialUsedUpMessage(FeatureTrialDto trial, string? planName) =>
        $"You have used all {trial.FreeUses} free {(trial.FreeUses == 1 ? trial.UnitSingular : trial.Unit)} of {trial.Label} included with your " +
        $"{(string.IsNullOrWhiteSpace(planName) ? "current" : planName)} plan. Upgrade to Premium to keep using it.";

    public async Task<IReadOnlyList<FeatureTrialDto>> GetTrialsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var access = await GetPlanAccessAsync(tenantId, cancellationToken);
        if (access.FreeUses.Count == 0)
            return Array.Empty<FeatureTrialDto>();

        var unpublished = PlatformFeatureSwitches.Unpublished(_configuration);
        var used = await LoadUsedAsync(tenantId, cancellationToken);
        return access.FreeUses
            .Where(t => !unpublished.Contains(t.Key))
            .OrderBy(t => t.Key)
            .Select(t => ToDto(t.Key, t.Value, used.GetValueOrDefault(t.Key)))
            .ToList();
    }

    public async Task<FeatureTrialDto?> GetTrialAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default)
    {
        var access = await GetPlanAccessAsync(tenantId, cancellationToken);
        if (!access.FreeUses.TryGetValue(feature, out var freeUses))
            return null;

        var used = await _context.TenantFeatureTrialUsages.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Feature == feature).Select(u => u.Used).FirstOrDefaultAsync(cancellationToken);
        return ToDto(feature, freeUses, used);
    }

    private static FeatureTrialDto ToDto(PlatformFeature feature, int freeUses, int used)
    {
        var unit = PlatformFeatureCatalog.TrialUnitFor(feature)!;
        return new FeatureTrialDto(feature, PlatformFeatureCatalog.For(feature).Label, freeUses, used, unit.Plural, unit.Singular);
    }

    public async Task RecordTrialUseAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default)
    {
        var now = _dateTime?.UtcNow ?? DateTime.UtcNow;

        // Used is a concurrency token, so two uses racing each other cannot both write the same count: the loser reads again and adds to it.
        for (var attempt = 1; ; attempt++)
        {
            var row = await _context.TenantFeatureTrialUsages.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Feature == feature, cancellationToken);
            if (row is null)
            {
                row = new TenantFeatureTrialUsage { TenantId = tenantId, Feature = feature, FirstUsedAtUtc = now };
                _context.TenantFeatureTrialUsages.Add(row);
            }

            row.Used++;
            row.LastUsedAtUtc = now;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Forget our copy (an added row is dropped, a loaded one detached) and read the winner's.
                _context.TenantFeatureTrialUsages.Entry(row).State = EntityState.Detached;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
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
    IReadOnlyList<FeatureCostDto> Costs,
    // Set when the plan does not include the feature but gives free uses of it: how many, and how many are gone.
    FeatureTrialDto? FreeTrial = null);

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
/// <param name="Trials">Features the plan does not include but gives free uses of. A feature listed here is in <paramref name="Features"/> only while it has uses left;
/// once they are gone the app shows it locked, with an upgrade prompt.</param>
public record EntitlementsDto(string? PlanCode, string? PlanName, IReadOnlyList<PlatformFeature> Features, decimal Balance, IReadOnlyList<CreditCostItemDto> Costs,
    IReadOnlyList<FeatureTrialDto>? Trials = null);

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

    /// <summary>Why the tenant cannot afford one unit of the scenario's action - not in its plan, not enough credits, or the feature's monthly cap used up - or null when it can.</summary>
    Task<QuotaShortfall?> GetShortfallAsync(Guid tenantId, CreditScenario scenario, CancellationToken cancellationToken = default);
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
        // A feature open on free uses is already in `included` (the access service counts it in while uses are left).
        var trials = await _access.GetTrialsAsync(tenantId, ct);
        return new EntitlementsDto(summary.PlanCode, summary.PlanName, included.Select(f => f.Feature).ToList(), summary.Balance, costs, trials);
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
        var trials = (await _access.GetTrialsAsync(tenantId, ct)).ToDictionary(t => t.Feature);

        // Campaigns spends nothing of its own (its sends are WhatsApp messaging), so it has no wallet, cap or ledger.
        var features = PlatformFeatureCatalog.All.Where(f => f.Scenarios.Count > 0).Select(f => new FeatureWalletDto(
            f.Feature, f.Label, f.Description, included.Contains(f.Feature),
            spent.GetValueOrDefault(f.Feature), caps.TryGetValue(f.Feature, out var cap) ? cap : null,
            f.Scenarios.Select(s =>
            {
                var info = CreditScenarioCatalog.For(s);
                return new FeatureCostDto(s, info.Title, info.UnitLabel, costs[s], info.UnitsConfigurable, info.Description);
            }).ToList(),
            trials.GetValueOrDefault(f.Feature))).ToList();

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
        var affordable = perUnit <= 0 ? spendable : Math.Floor(spendable / perUnit);

        // On free uses, a lead discovery run must not find more leads than the tenant has free leads left.
        if (scenario == CreditScenario.LeadDiscovery && await _access.GetTrialAsync(tenantId, feature, ct) is { } trial)
            affordable = Math.Min(affordable, trial.Remaining);
        return affordable;
    }

    public async Task<QuotaShortfall?> GetShortfallAsync(Guid tenantId, CreditScenario scenario, CancellationToken ct = default)
    {
        var feature = PlatformFeatureCatalog.FeatureOf(scenario);
        var name = FeatureName(feature);

        if (!await _access.HasAsync(tenantId, feature, ct))
        {
            if (await _access.GetTrialAsync(tenantId, feature, ct) is { } trial)
            {
                var planName = await _context.Subscriptions.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenantId)
                    .Join(_context.Plans, s => s.PlanId, p => (Guid?)p.Id, (s, p) => p.Name).FirstOrDefaultAsync(ct);
                return new QuotaShortfall(QuotaShortfallKind.NotInPlan, FeatureAccessService.TrialUsedUpMessage(trial, planName));
            }

            return new QuotaShortfall(QuotaShortfallKind.NotInPlan, $"{name} is not included in your plan. Upgrade your plan to use it.");
        }

        var perUnit = Math.Max(0m, await _costs.GetCreditsPerUnitAsync(scenario, ct));
        var balance = (await _ledger.GetBalancesAsync(tenantId, ct)).Single(b => b.QuotaType == QuotaType.Credits).Balance;
        if (balance < Math.Max(perUnit, 0.0001m))
            return new QuotaShortfall(QuotaShortfallKind.InsufficientCredits, $"Your wallet has {balance:0.##} credits, which is not enough for {name}. Buy credits to continue.");

        if (await GetCapRemainingAsync(tenantId, feature, ct) is { } remaining && remaining < Math.Max(perUnit, 0.0001m))
        {
            var cap = await _context.TenantFeatureCaps.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Feature == feature).Select(c => c.MonthlyLimit).FirstAsync(ct);
            var resets = MonthStart(_dateTime.UtcNow).AddMonths(1);
            return new QuotaShortfall(
                QuotaShortfallKind.MonthlyLimitReached,
                $"{name} has used its monthly limit of {cap:0.##} credits. Your wallet still has {balance:0.##} credits. " +
                $"The limit resets on {resets:d MMM yyyy}; to continue sooner, raise it in Wallet.");
        }

        return null;
    }

    private static string FeatureName(PlatformFeature feature) => feature switch
    {
        PlatformFeature.WhatsAppMessaging => "WhatsApp messaging",
        PlatformFeature.AiConversations => "AI conversations",
        PlatformFeature.LeadDiscovery => "Lead discovery",
        PlatformFeature.ContentStudio => "Content Studio",
        PlatformFeature.ProductKnowledge => "Product knowledge",
        PlatformFeature.Campaigns => "Campaigns",
        _ => feature.ToString()
    };

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
                // A plan paid for through a Razorpay mandate gets its credits from each confirmed payment (AutoPayEntitlements), not from the
                // calendar: granting here as well would hand the month out twice.
                where p.MonthlyCredits > 0 && s.Status == SubscriptionStatus.Active && s.RazorpaySubscriptionRowId == null
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
