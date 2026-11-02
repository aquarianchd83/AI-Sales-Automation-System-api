using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>A credit pack as the margin is read against it: what it sells for in India (before tax) and so what one credit bought in it is worth.</summary>
public record ServiceMarginPackDto(Guid Id, string Name, decimal Credits, decimal PriceInr, decimal InrPerCredit);

/// <summary>What one item of a service earns when the credits come from one pack: the credits it draws at that pack's rupee value per credit, less the cost.</summary>
public record ServiceMarginByPackDto(Guid PackId, decimal ChargeInr, decimal? MarginInr, decimal? MarginPercent);

/// <summary>One line of the Service margins screen: what one unit of a service costs the platform, what it draws from a tenant's credits, and what that
/// leaves per credit pack. <paramref name="CreditsPerUnit"/> is the setting (credits per reply, per candidate, per marketing-equivalent message);
/// <paramref name="CreditsCharged"/> is what one item actually draws - the same, except for WhatsApp, where a cheaper category draws a fraction of it.</summary>
public record ServiceMarginRowDto(
    CreditScenario Scenario,
    string Title,
    string UnitLabel,
    // Null when the platform does not record what the service costs it (Content Studio) or has no calls to work it out from yet. The charge is still shown; there is no margin.
    decimal? CostUsd,
    decimal? CostInr,
    // Where the cost came from: the last 30 days of real calls, or the price list and typical usage when there are none yet.
    string CostBasis,
    int Samples,
    // The platform cannot work this cost out, so the admin may type it (Content Studio); CostIsManual says the figure shown is theirs.
    bool CostEditable,
    bool CostIsManual,
    decimal CreditsPerUnit,
    // How many of those units one item draws: 1 for a reply or a candidate, a fraction or more for a WhatsApp message (by its category).
    decimal UnitsPerItem,
    decimal CreditsCharged,
    // The platform pays for this one out of its own wallet, so the tenant is not charged at all.
    bool PaidByPlatform,
    IReadOnlyList<ServiceMarginByPackDto> ByPack);

public record ServiceMarginDto(decimal InrPerUsd, IReadOnlyList<ServiceMarginPackDto> Packs, IReadOnlyList<ServiceMarginRowDto> Rows, IReadOnlyList<string> Notes);

public record ServiceMarginChargeInput(CreditScenario Scenario, decimal CreditsPerUnit);

/// <summary>What a service costs per unit, in rupees, as the admin types it. Null clears it.</summary>
public record ServiceMarginManualCostInput(CreditScenario Scenario, decimal? CostInr);

public record UpdateServiceMarginRequest(IReadOnlyList<ServiceMarginChargeInput> Charges, IReadOnlyList<ServiceMarginManualCostInput>? ManualCosts = null);

public interface IServiceMarginService
{
    Task<ServiceMarginDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the credits each service draws. That is the platform admin's to set; the cost is never typed, and what a credit is worth comes from the pack it was bought in.</summary>
    Task<ServiceMarginDto> UpdateAsync(UpdateServiceMarginRequest request, Guid actorUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cost, charge and margin for the three things a tenant is charged for. The cost is worked out, never typed: from the AI calls and discovery runs of the
/// last 30 days when there are any, otherwise from the price lists on the Configuration page and the typical usage set beside them (the same figures a
/// plan is costed with). A tenant is charged in credits, and a credit is worth what the pack it was bought in sold it for (India price, before tax, over the
/// pack's credits), so the margin is shown for each pack. Credits that come free with a plan are not a pack and are not shown.
/// WhatsApp is always from the price list and category mix: what a message draws depends on its category, which a plain average of messages would hide.
/// </summary>
public class ServiceMarginService : IServiceMarginService
{
    private const int SampleDays = 30;
    private const decimal EstimateUnits = 100_000m;
    private const string Country = "IN";

    // Every scenario the application spends credits for, in the order the Credit Costs screen lists them.
    private static readonly CreditScenario[] Scenarios = CreditScenarioCatalog.All.Select(i => i.Scenario).ToArray();

    private readonly IApplicationDbContext _context;
    private readonly IPlanCostReportService _costs;
    private readonly ICreditPayerService _credits;
    private readonly IDateTimeProvider _dateTime;
    private readonly IOptionsSnapshot<FxOptions> _fx;
    private readonly IOptionsSnapshot<ManualServiceCostOptions> _manual;
    private readonly IAppSettingsStore _store;
    private readonly IOptionsSnapshot<WhatsAppPricingOptions> _whatsApp;

    private const string ManualPrefix = "ServiceCost:";

    // The services whose cost the platform cannot record itself, and the setting that holds the cost typed for each.
    private static readonly Dictionary<CreditScenario, string> ManualKeys = new()
    {
        [CreditScenario.ContentStudioGeneration] = ManualPrefix + nameof(ManualServiceCostOptions.ContentStudioGenerationInr),
        [CreditScenario.ContentStudioVideoRender] = ManualPrefix + nameof(ManualServiceCostOptions.ContentStudioVideoRenderInr),
    };

    public ServiceMarginService(
        IApplicationDbContext context, IPlanCostReportService costs, ICreditPayerService credits, IDateTimeProvider dateTime, IOptionsSnapshot<FxOptions> fx,
        IOptionsSnapshot<ManualServiceCostOptions> manual, IAppSettingsStore store, IOptionsSnapshot<WhatsAppPricingOptions> whatsApp)
    {
        _whatsApp = whatsApp;
        _context = context;
        _costs = costs;
        _credits = credits;
        _dateTime = dateTime;
        _fx = fx;
        _manual = manual;
        _store = store;
    }

    public async Task<ServiceMarginDto> UpdateAsync(UpdateServiceMarginRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        foreach (var charge in request.Charges)
        {
            if (!Scenarios.Contains(charge.Scenario))
                throw Invalid(nameof(request.Charges), "That is not something credits are spent on.");
            if (charge.CreditsPerUnit < 0 || charge.CreditsPerUnit > 100_000)
                throw Invalid(nameof(request.Charges), "Credits per unit cannot be negative.");
        }

        foreach (var cost in request.ManualCosts ?? Array.Empty<ServiceMarginManualCostInput>())
        {
            if (!ManualKeys.ContainsKey(cost.Scenario))
                throw Invalid(nameof(request.ManualCosts), "This cost is worked out for you; only Content Studio costs are typed in.");
            if (cost.CostInr is < 0 or > 1_000_000)
                throw Invalid(nameof(request.ManualCosts), "A cost cannot be negative.");
        }

        var current = (await _credits.GetScenariosAsync(cancellationToken)).ToDictionary(s => s.Scenario);
        foreach (var charge in request.Charges)
            await _credits.SetPayerAsync(charge.Scenario, new UpdateCreditPayerRequest(current[charge.Scenario].Payer, charge.CreditsPerUnit), actorUserId, cancellationToken);

        // The typed costs are written as a whole: one the request leaves out keeps what it had, and one sent empty is cleared.
        var manual = ManualKeys.Keys.ToDictionary(s => s, s => ManualCostOf(_manual.Value, s));
        foreach (var cost in request.ManualCosts ?? Array.Empty<ServiceMarginManualCostInput>())
            manual[cost.Scenario] = cost.CostInr is { } inr ? Math.Round(inr, 4, MidpointRounding.AwayFromZero) : null;

        if (request.ManualCosts is { Count: > 0 })
        {
            var rows = manual.Where(m => m.Value is not null).ToDictionary(
                m => ManualKeys[m.Key], m => (string?)m.Value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await _store.ReplacePrefixesAsync(rows, new[] { ManualPrefix }, actorUserId, cancellationToken);
        }

        // The options bound for this request still hold what was there before, so the answer is built from what was just stored.
        return await BuildAsync(manual, cancellationToken);
    }

    public Task<ServiceMarginDto> GetAsync(CancellationToken cancellationToken = default) => BuildAsync(null, cancellationToken);

    private static decimal? ManualCostOf(ManualServiceCostOptions options, CreditScenario scenario) => scenario switch
    {
        CreditScenario.ContentStudioGeneration => options.ContentStudioGenerationInr,
        CreditScenario.ContentStudioVideoRender => options.ContentStudioVideoRenderInr,
        _ => null
    };

    private async Task<ServiceMarginDto> BuildAsync(IReadOnlyDictionary<CreditScenario, decimal?>? manualCosts, CancellationToken cancellationToken)
    {
        var fx = _fx.Value.InrPerUsd;
        var since = _dateTime.UtcNow.AddDays(-SampleDays);
        var notes = new List<string>();

        var settings = (await _credits.GetScenariosAsync(cancellationToken)).ToDictionary(s => s.Scenario);
        var packs = await GetPacksAsync(cancellationToken);
        if (packs.Count == 0)
            notes.Add("No active credit pack has an India price, so there is no pack to measure the charge against. Add one on the Package page.");

        // The estimate, for whichever service has no real calls to learn from.
        var estimate = await _costs.BuildAsync(new PlanCostReportRequest(new[]
        {
            new PlanQuotaInput(QuotaType.AiConversations, EstimateUnits),
            new PlanQuotaInput(QuotaType.LeadCandidates, EstimateUnits)
        }), cancellationToken);
        notes.AddRange(estimate.Warnings.Where(w => w.StartsWith("No default") || w.Contains("mix")));

        // ---- AI conversation: the average of what each reply really cost.
        var replies = _context.AiExecutions.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.CreatedAt >= since && e.Mode == AiExecutionMode.Live && e.Status == AiExecutionStatus.Succeeded
                        && e.Task == AiTaskKinds.ConversationReply && e.EstimatedCostUsd > 0);
        var replyCount = await replies.CountAsync(cancellationToken);
        var aiCost = replyCount > 0 ? await replies.SumAsync(e => e.EstimatedCostUsd!.Value, cancellationToken) / replyCount : estimate.Ai.CostPerConversationUsd;

        // ---- Lead discovery: what the runs cost, over the candidates they looked at.
        var runs = _context.LeadDiscoveryRuns.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.RanAtUtc >= since && r.CandidatesConsidered > 0 && r.EstimatedCostUsd > 0);
        var candidates = await runs.SumAsync(r => (long)r.CandidatesConsidered, cancellationToken);
        var leadCost = candidates > 0 ? await runs.SumAsync(r => r.EstimatedCostUsd, cancellationToken) / candidates : estimate.Leads.CostPerCandidateUsd;

        // ---- WhatsApp: a marketing message, in India (where the platform admin and most tenants are). That is the message the credit price is set for - a utility
        // or authentication message draws fewer credits in the same proportion as it costs Meta less - so the credits here are the ones tenants are shown.
        var waCost = _whatsApp.Value.RatesFor(Country).Marketing;

        // ---- Product knowledge: the AI calls of the last 30 days that belong to the scenario, over the actions tenants were charged for.
        var knowledgeCalls = (await _context.AiExecutions.AsNoTracking().IgnoreQueryFilters()
                .Where(e => e.CreatedAt >= since && e.Mode == AiExecutionMode.Live && e.Status == AiExecutionStatus.Succeeded && e.EstimatedCostUsd > 0)
                .GroupBy(e => e.Task)
                .Select(g => new { Task = g.Key, Calls = g.Count(), Cost = g.Sum(e => e.EstimatedCostUsd!.Value) })
                .ToListAsync(cancellationToken))
            .Select(g => (Scenario: CreditScenarioCatalog.ForAiTask(g.Task), g.Calls, g.Cost))
            .Where(g => g.Scenario is CreditScenario.ProductKnowledgeReading or CreditScenario.ProductKnowledgeAnswering)
            .GroupBy(g => g.Scenario!.Value)
            .ToDictionary(g => g.Key, g => (Calls: g.Sum(x => x.Calls), Cost: g.Sum(x => x.Cost)));
        var knowledgeActions = await _context.CreditUsages.AsNoTracking()
            .Where(u => u.OccurredAtUtc >= since && u.Status != CreditUsageStatus.Unpaid
                        && (u.Scenario == CreditScenario.ProductKnowledgeReading || u.Scenario == CreditScenario.ProductKnowledgeAnswering))
            .GroupBy(u => u.Scenario)
            .Select(g => new { Scenario = g.Key, Actions = g.Count() })
            .ToDictionaryAsync(g => g.Scenario, g => g.Actions, cancellationToken);

        (decimal? Cost, string Basis, int Samples) Knowledge(CreditScenario scenario)
        {
            if (!knowledgeCalls.TryGetValue(scenario, out var calls))
                return (null, $"No AI calls for this recorded in the last {SampleDays} days, so there is nothing to work a cost from yet", 0);

            // Charged per action, and an action can take several calls: the cost of the calls over the actions, when the actions are known.
            return knowledgeActions.TryGetValue(scenario, out var actions) && actions > 0
                ? (calls.Cost / actions, $"{calls.Calls:N0} AI calls over {actions:N0} charged actions in the last {SampleDays} days", actions)
                : (calls.Cost / calls.Calls, $"Average of {calls.Calls:N0} AI calls in the last {SampleDays} days", calls.Calls);
        }

        ServiceMarginRowDto Row(CreditScenario scenario, string title, string unit, decimal? costUsd, string basis, int samples, decimal unitsPerItem, bool editable = false, bool manual = false)
        {
            var setting = settings[scenario];
            var perUnit = setting.UnitsPerAction ?? 0m;
            var credits = Math.Round(perUnit * unitsPerItem, 4);
            decimal? cost = costUsd is { } usd ? Math.Round(usd * fx, 4, MidpointRounding.AwayFromZero) : null;
            var byPack = packs.Select(p =>
            {
                var charge = Math.Round(credits * p.InrPerCredit, 4, MidpointRounding.AwayFromZero);
                var margin = cost is { } c ? charge - c : (decimal?)null;
                return new ServiceMarginByPackDto(p.Id, charge, margin, margin is { } m && charge > 0 ? Math.Round(m / charge * 100m, 1) : null);
            }).ToList();

            return new ServiceMarginRowDto(
                scenario, title, unit, costUsd is { } u ? Math.Round(u, 6) : null, cost, basis, samples, editable, manual, perUnit, Math.Round(unitsPerItem, 4), credits,
                setting.Payer == CreditPayer.Platform, byPack);
        }

        ServiceMarginRowDto Plain(CreditScenario scenario, decimal? costUsd, string basis, int samples)
        {
            var info = CreditScenarioCatalog.For(scenario);
            return Row(scenario, info.Title, info.UnitLabel, costUsd, basis, samples, 1m);
        }

        // Content Studio: nothing records what a step costs the platform, so the cost is what the admin typed, if anything.
        ServiceMarginRowDto Studio(CreditScenario scenario)
        {
            var info = CreditScenarioCatalog.For(scenario);
            var typed = manualCosts is not null ? manualCosts.GetValueOrDefault(scenario) : ManualCostOf(_manual.Value, scenario);
            var costUsd = typed is { } inr && fx > 0 ? inr / fx : (decimal?)null;
            var basis = typed is null
                ? "Not tracked: the platform does not record what this costs it. Type what a unit costs you to see a margin."
                : "Typed in by you: the platform does not record what this costs it";
            return Row(scenario, info.Title, info.UnitLabel, costUsd, basis, 0, 1m, editable: true, manual: typed is not null);
        }
        var reading = Knowledge(CreditScenario.ProductKnowledgeReading);
        var answering = Knowledge(CreditScenario.ProductKnowledgeAnswering);

        // The order of the Credit Costs screen.
        var rows = new List<ServiceMarginRowDto>
        {
            Row(CreditScenario.CustomerChatReply, "AI conversation", "reply", aiCost,
                replyCount > 0 ? $"Average of {replyCount:N0} AI replies in the last {SampleDays} days" : "Estimate from the AI price list and typical tokens per reply",
                replyCount, 1m),
            Row(CreditScenario.WhatsAppTemplateSend, "WhatsApp message", "marketing message", waCost,
                "The WhatsApp price list for a marketing message in India. A utility or authentication message costs less and draws fewer credits, in the same proportion",
                0, 1m),
            Row(CreditScenario.LeadDiscovery, "Discover customers", "candidate", leadCost,
                candidates > 0 ? $"Discovery runs in the last {SampleDays} days: {candidates:N0} candidates looked at" : "Estimate from the lead discovery price list and typical usage",
                (int)Math.Min(candidates, int.MaxValue), 1m),
            Studio(CreditScenario.ContentStudioGeneration),
            Studio(CreditScenario.ContentStudioVideoRender),
            Plain(CreditScenario.ProductKnowledgeReading, reading.Cost, reading.Basis, reading.Samples),
            Plain(CreditScenario.ProductKnowledgeAnswering, answering.Cost, answering.Basis, answering.Samples)
        };

        return new ServiceMarginDto(fx, packs, rows, notes);
    }

    /// <summary>The credit packs on sale, cheapest credit last: each with what it sells for in India and so what one of its credits is worth.</summary>
    private async Task<IReadOnlyList<ServiceMarginPackDto>> GetPacksAsync(CancellationToken cancellationToken)
    {
        var rows = await (
            from pack in _context.CreditPacks.AsNoTracking()
            join price in _context.CreditPackPrices.AsNoTracking() on pack.Id equals price.CreditPackId
            where pack.IsActive && pack.QuotaType == QuotaType.Credits && pack.Units > 0 && price.CountryCode == Country && price.Amount > 0
            select new { pack.Id, pack.Name, pack.Units, price.Amount })
            .ToListAsync(cancellationToken);

        return rows
            .Select(p => new ServiceMarginPackDto(p.Id, p.Name, p.Units, p.Amount, Math.Round(p.Amount / p.Units, 4, MidpointRounding.AwayFromZero)))
            .OrderByDescending(p => p.InrPerCredit)
            .ToList();
    }

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
