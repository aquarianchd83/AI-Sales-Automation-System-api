using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>What a plan's quotas cost the platform to serve, depends on how they get used. These are the usage
/// assumptions the estimate rests on - the operator can change any of them and the report recalculates.</summary>
public record PlanCostAssumptionsDto(
    // How the pooled WhatsApp units are spent, by template category. Shares are relative; they need not add to 100.
    decimal MarketingSharePercent,
    decimal UtilitySharePercent,
    decimal AuthenticationSharePercent,
    // One AI conversation, in tokens.
    int PromptTokensPerConversation,
    int CompletionTokensPerConversation,
    // One lead candidate considered by a discovery run (fresh, duplicate or rejected all count), in tokens and searches.
    int InputTokensPerCandidate,
    int OutputTokensPerCandidate,
    decimal WebSearchesPerCandidate);

/// <summary>The starting assumptions, and where the AI and lead figures came from: real usage on this platform, or built-in guesses.</summary>
public record PlanCostDefaultsDto(PlanCostAssumptionsDto Assumptions, string AiSource, string LeadSource);

public record PlanCostReportRequest(
    IReadOnlyList<PlanQuotaInput> IncludedQuotas,
    IReadOnlyList<CountryPriceInput>? CountryPrices = null,
    PlanCostAssumptionsDto? Assumptions = null);

public record PlanCostCategoryDto(string Category, decimal SharePercent, decimal QuotaWeight, decimal Messages);

public record PlanCostAiDto(decimal Units, string? Model, decimal CostPerConversationUsd, decimal TotalUsd);

public record PlanCostLeadDto(decimal Units, string Model, decimal CostPerCandidateUsd, decimal TotalUsd);

/// <summary>One country's view of the plan: what it sells for there, what it costs to serve there, and the margin. The
/// WhatsApp share differs by country (Meta prices by country); AI and lead discovery cost the same everywhere. A country with no
/// price set is not sold (<see cref="IsSold"/> false) and has no margin. Local amounts are in that country's currency; the
/// platform admin is in India, so the Inr figures are what they read.</summary>
public record PlanCostCountryDto(
    string CountryCode,
    string CountryName,
    string CurrencyCode,
    string CurrencySymbol,
    decimal PriceLocal,
    bool IsCustomPrice,
    decimal WhatsAppCostUsd,
    decimal AiCostUsd,
    decimal LeadCostUsd,
    decimal TotalCostUsd,
    decimal CostLocal,
    decimal MarginLocal,
    decimal? MarginPercent,
    bool IsSold,
    decimal PriceInr,
    decimal CostInr,
    decimal MarginInr);

public record PlanCostReportDto(
    decimal WhatsAppUnits,
    decimal WhatsAppMessagesEstimate,
    IReadOnlyList<PlanCostCategoryDto> WhatsAppCategories,
    PlanCostAiDto Ai,
    PlanCostLeadDto Leads,
    IReadOnlyList<PlanCostCountryDto> Countries,
    IReadOnlyList<string> Warnings,
    PlanCostAssumptionsDto Assumptions,
    // Rupees per dollar - what the USD unit costs were converted at, so the page can show a per-unit cost in rupees.
    decimal InrPerUsd);

public interface IPlanCostReportService
{
    Task<PlanCostDefaultsDto> GetDefaultsAsync(CancellationToken cancellationToken = default);

    /// <summary>The consolidated cost-and-margin report for a plan that is being designed - nothing is saved.</summary>
    Task<PlanCostReportDto> BuildAsync(PlanCostReportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Prices a plan from the Configuration page's charges, so the operator no longer works it out by hand. The unit costs
/// are: a WhatsApp pool unit = the category mix spent at each category's per-message rate, divided by how many pool units
/// that mix uses (the quota weights); an AI conversation = its tokens at the default AI model's rates; a lead candidate =
/// its tokens and searches at the default lead discovery model's rates. All estimates from list prices - see the pricing
/// options for why they are not an invoice.
/// </summary>
public class PlanCostReportService : IPlanCostReportService
{
    // Used when there is no history to learn from: a small support conversation and a lightly researched candidate.
    private static readonly PlanCostAssumptionsDto BuiltIn = new(60, 30, 10, 1500, 300, 6000, 800, 0.5m);

    private readonly IApplicationDbContext _context;
    private readonly ICountryAvailability _countries;
    private readonly IDateTimeProvider _dateTime;
    private readonly WhatsAppPricingOptions _whatsAppPricing;
    private readonly LeadDiscoveryPricingOptions _leadPricing;
    private readonly AiPricingOptions _aiPricing;
    private readonly FxOptions _fx;
    private readonly CostAssumptionsOptions _costing;

    public PlanCostReportService(
        IApplicationDbContext context,
        ICountryAvailability countries,
        IDateTimeProvider dateTime,
        IOptionsSnapshot<WhatsAppPricingOptions> whatsAppPricing,
        IOptionsSnapshot<LeadDiscoveryPricingOptions> leadPricing,
        IOptionsSnapshot<AiPricingOptions> aiPricing,
        IOptionsSnapshot<FxOptions> fx,
        IOptionsSnapshot<CostAssumptionsOptions> costing)
    {
        _fx = fx.Value;
        _costing = costing.Value;
        _context = context;
        _countries = countries;
        _dateTime = dateTime;
        _whatsAppPricing = whatsAppPricing.Value;
        _leadPricing = leadPricing.Value;
        _aiPricing = aiPricing.Value;
    }

    public async Task<PlanCostDefaultsDto> GetDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var since = _dateTime.UtcNow.AddDays(-90);

        // What one AI reply actually used across the platform lately. Averages of what happened beat any guess.
        var ai = _context.AiInteractions.IgnoreQueryFilters()
            .Where(a => a.CreatedAt >= since && a.PromptTokens > 0 && a.CompletionTokens > 0);
        var aiCount = await ai.CountAsync(cancellationToken);
        var aiPrompt = BuiltIn.PromptTokensPerConversation;
        var aiCompletion = BuiltIn.CompletionTokensPerConversation;
        var aiSource = "Built-in estimate - no AI conversations recorded yet.";
        if (aiCount > 0)
        {
            aiPrompt = (int)Math.Round(await ai.AverageAsync(a => (double)a.PromptTokens!.Value, cancellationToken));
            aiCompletion = (int)Math.Round(await ai.AverageAsync(a => (double)a.CompletionTokens!.Value, cancellationToken));
            aiSource = $"Average of {aiCount:N0} AI conversations in the last 90 days.";
        }

        // Per candidate CONSIDERED - fresh, duplicate and rejected all draw quota, so all carry cost.
        var runs = _context.LeadDiscoveryRuns.IgnoreQueryFilters().Where(r => r.RanAtUtc >= since && r.CandidatesConsidered > 0);
        var candidates = await runs.SumAsync(r => (long)r.CandidatesConsidered, cancellationToken);
        var leadInput = BuiltIn.InputTokensPerCandidate;
        var leadOutput = BuiltIn.OutputTokensPerCandidate;
        var leadSearches = BuiltIn.WebSearchesPerCandidate;
        var leadSource = "Built-in estimate - no discovery runs recorded yet.";
        if (candidates > 0)
        {
            // Cached input is counted as plain input: a slight overstatement, which is the safe direction for a margin check.
            var input = await runs.SumAsync(r => (long)r.InputTokens + r.CacheReadTokens + r.CacheWriteTokens, cancellationToken);
            var output = await runs.SumAsync(r => (long)r.OutputTokens, cancellationToken);
            var searches = await runs.SumAsync(r => (long)r.WebSearches, cancellationToken);
            leadInput = (int)Math.Round(input / (decimal)candidates);
            leadOutput = (int)Math.Round(output / (decimal)candidates);
            leadSearches = Math.Round(searches / (decimal)candidates, 2);
            leadSource = $"Average of {candidates:N0} candidates across discovery runs in the last 90 days.";
        }

        return new PlanCostDefaultsDto(
            new PlanCostAssumptionsDto(
                BuiltIn.MarketingSharePercent, BuiltIn.UtilitySharePercent, BuiltIn.AuthenticationSharePercent,
                aiPrompt, aiCompletion, leadInput, leadOutput, leadSearches),
            aiSource, leadSource);
    }

    public async Task<PlanCostReportDto> BuildAsync(PlanCostReportRequest request, CancellationToken cancellationToken = default)
    {
        // The typical-usage figures set once on the Configuration page. A request can still carry its own (tests, what-ifs).
        var given = request.Assumptions ?? new PlanCostAssumptionsDto(
            _costing.MarketingSharePercent, _costing.UtilitySharePercent, _costing.AuthenticationSharePercent,
            _costing.PromptTokensPerConversation, _costing.CompletionTokensPerConversation,
            _costing.InputTokensPerCandidate, _costing.OutputTokensPerCandidate, _costing.WebSearchesPerCandidate);
        // Nothing an operator types can make a cost negative.
        var a = given with
        {
            MarketingSharePercent = Math.Max(0m, given.MarketingSharePercent),
            UtilitySharePercent = Math.Max(0m, given.UtilitySharePercent),
            AuthenticationSharePercent = Math.Max(0m, given.AuthenticationSharePercent),
            PromptTokensPerConversation = Math.Max(0, given.PromptTokensPerConversation),
            CompletionTokensPerConversation = Math.Max(0, given.CompletionTokensPerConversation),
            InputTokensPerCandidate = Math.Max(0, given.InputTokensPerCandidate),
            OutputTokensPerCandidate = Math.Max(0, given.OutputTokensPerCandidate),
            WebSearchesPerCandidate = Math.Max(0m, given.WebSearchesPerCandidate)
        };
        var warnings = new List<string>();

        decimal Units(QuotaType type) => request.IncludedQuotas.Where(q => q.QuotaType == type).Sum(q => Math.Max(0m, q.Units));
        var waUnits = Units(QuotaType.WhatsAppMessages);
        var aiUnits = Units(QuotaType.AiConversations);
        var leadUnits = Units(QuotaType.LeadCandidates);

        // ---- WhatsApp: pool units -> messages by category
        var shares = new (TemplateCategory Category, decimal Share, decimal Weight)[]
        {
            (TemplateCategory.Marketing, Math.Max(0m, a.MarketingSharePercent), Quota.WhatsAppQuotaWeights.For(_whatsAppPricing, TemplateCategory.Marketing)),
            (TemplateCategory.Utility, Math.Max(0m, a.UtilitySharePercent), Quota.WhatsAppQuotaWeights.For(_whatsAppPricing, TemplateCategory.Utility)),
            (TemplateCategory.Authentication, Math.Max(0m, a.AuthenticationSharePercent), Quota.WhatsAppQuotaWeights.For(_whatsAppPricing, TemplateCategory.Authentication))
        };
        var totalShare = shares.Sum(s => s.Share);
        if (totalShare <= 0 && waUnits > 0)
            warnings.Add("The WhatsApp category mix is all zero, so WhatsApp cost cannot be estimated.");

        // Pool units used per message on average, given the mix.
        var unitsPerMessage = totalShare <= 0 ? 0m : shares.Sum(s => s.Share / totalShare * s.Weight);
        var messages = unitsPerMessage <= 0 ? 0m : waUnits / unitsPerMessage;
        var categories = shares
            .Select(s => new PlanCostCategoryDto(
                s.Category.ToString(), totalShare <= 0 ? 0m : Math.Round(s.Share / totalShare * 100m, 2), s.Weight,
                totalShare <= 0 ? 0m : Math.Round(messages * s.Share / totalShare, 0)))
            .ToList();

        // ---- AI conversations
        AiModelRates? aiRates = string.IsNullOrWhiteSpace(_aiPricing.DefaultModel) ? null : _aiPricing.Models.GetValueOrDefault(_aiPricing.DefaultModel);
        var aiPer = aiRates is null ? 0m : a.PromptTokensPerConversation / 1000m * aiRates.PromptPer1K + a.CompletionTokensPerConversation / 1000m * aiRates.CompletionPer1K;
        if (aiUnits > 0 && aiRates is null)
            warnings.Add("No default AI model is chosen on the Configuration page, so AI conversations are counted as free.");

        // ---- Lead discovery
        var leadRates = _leadPricing.RatesFor(null);
        var leadModel = string.IsNullOrWhiteSpace(_leadPricing.DefaultModel) ? "fallback rates" : _leadPricing.DefaultModel!;
        if (leadUnits > 0 && string.IsNullOrWhiteSpace(_leadPricing.DefaultModel))
            warnings.Add("No default lead discovery model is chosen on the Configuration page, so the fallback rates are used.");
        var leadPer = (a.InputTokensPerCandidate * leadRates.InputPerMillion + a.OutputTokensPerCandidate * leadRates.OutputPerMillion) / 1_000_000m
                      + a.WebSearchesPerCandidate * _leadPricing.WebSearchPerThousand / 1000m;

        var aiTotal = aiUnits * aiPer;
        var leadTotal = leadUnits * leadPer;

        // ---- Per country
        var customPrices = (request.CountryPrices ?? Array.Empty<CountryPriceInput>())
            .Where(p => p.Amount > 0)
            .ToDictionary(p => p.CountryCode.ToUpperInvariant(), p => p.Amount);

        var rows = new List<PlanCostCountryDto>();
        foreach (var region in (await _countries.GetEnabledAsync(cancellationToken))
                     .GroupBy(r => r.CountryCode, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(r => r.CountryName))
        {
            var rates = _whatsAppPricing.RatesFor(region.CountryCode);
            var waCost = totalShare <= 0 ? 0m : messages * shares.Sum(s => s.Share / totalShare * RateFor(rates, s.Category));

            // A price exists only where one was set - there is no converted fallback, so an unpriced country is simply not sold.
            var isSold = customPrices.TryGetValue(region.CountryCode.ToUpperInvariant(), out var priceLocal);

            var totalUsd = waCost + aiTotal + leadTotal;
            var costLocal = Math.Round(totalUsd * region.RateToUsd, 2, MidpointRounding.AwayFromZero);
            var costInr = Math.Round(totalUsd * _fx.InrPerUsd, 2, MidpointRounding.AwayFromZero);
            var priceInr = isSold ? Math.Round(priceLocal / region.RateToUsd * _fx.InrPerUsd, 2, MidpointRounding.AwayFromZero) : 0m;
            var marginLocal = isSold ? priceLocal - costLocal : 0m;

            rows.Add(new PlanCostCountryDto(
                region.CountryCode, region.CountryName, region.CurrencyCode, region.CurrencySymbol,
                isSold ? priceLocal : 0m, isSold,
                Math.Round(waCost, 4), Math.Round(aiTotal, 4), Math.Round(leadTotal, 4), Math.Round(totalUsd, 4),
                costLocal, marginLocal, isSold && priceLocal > 0 ? Math.Round(marginLocal / priceLocal * 100m, 1) : null,
                isSold, priceInr, costInr, isSold ? priceInr - costInr : 0m));
        }

        var losing = rows.Where(r => r.IsSold && r.MarginLocal < 0).Select(r => r.CountryName).ToList();
        if (losing.Count > 0)
            warnings.Add($"At these prices the plan loses money in: {string.Join(", ", losing)}.");
        if (customPrices.Count == 0)
            warnings.Add("No country has a price yet, so the plan cannot be sold anywhere.");
        else if (rows.Any(r => !r.IsSold))
            warnings.Add($"Not sold (no price set) in: {string.Join(", ", rows.Where(r => !r.IsSold).Select(r => r.CountryName))}.");

        return new PlanCostReportDto(
            waUnits, Math.Round(messages, 0), categories,
            new PlanCostAiDto(aiUnits, string.IsNullOrWhiteSpace(_aiPricing.DefaultModel) ? null : _aiPricing.DefaultModel!.Replace('/', ':'), Math.Round(aiPer, 6), Math.Round(aiTotal, 4)),
            new PlanCostLeadDto(leadUnits, leadModel, Math.Round(leadPer, 6), Math.Round(leadTotal, 4)),
            rows, warnings, a, _fx.InrPerUsd);
    }

    private static decimal RateFor(WhatsAppCategoryRates rates, TemplateCategory category) => rates.For(category);
}
