using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformBillingService : IPlatformBillingService
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserPricingService _pricing;
    private readonly IValidator<CreatePlanRequest> _createValidator;
    private readonly IValidator<UpdatePlanRequest> _updateValidator;
    private readonly IValidator<CreateCreditPackRequest> _createPackValidator;
    private readonly IValidator<UpdateCreditPackRequest> _updatePackValidator;

    public PlatformBillingService(
        IApplicationDbContext context,
        ICurrentUserPricingService pricing,
        IValidator<CreatePlanRequest> createValidator,
        IValidator<UpdatePlanRequest> updateValidator,
        IValidator<CreateCreditPackRequest> createPackValidator,
        IValidator<UpdateCreditPackRequest> updatePackValidator)
    {
        _context = context;
        _pricing = pricing;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _createPackValidator = createPackValidator;
        _updatePackValidator = updatePackValidator;
    }

    public async Task<IReadOnlyList<PlatformPlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
    {
        // Materialised before mapping: the local price is a conversion the database knows nothing about.
        var pricing = await _pricing.GetAsync(cancellationToken);

        var plans = await _context.Plans
            .OrderBy(p => p.PriceMonthlyCents)
            .ToListAsync(cancellationToken);

        var planIds = plans.Select(p => p.Id).ToList();
        var featuresByPlan = await LoadFeaturesAsync(planIds, cancellationToken);
        var pricesByPlan = await CatalogPricing.LoadPlanPricesAsync(_context, planIds, cancellationToken);
        return plans.Select(p => ToDto(p, pricing, featuresByPlan.GetValueOrDefault(p.Id), pricesByPlan.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<PagedResult<PlatformSubscriptionListItemDto>> GetSubscriptionsAsync(PlatformSubscriptionQuery query, CancellationToken cancellationToken = default)
    {
        var subscriptions = _context.Subscriptions.IgnoreQueryFilters().AsQueryable();

        if (query.Status is { } status)
            subscriptions = subscriptions.Where(s => s.Status == status);

        var joined = subscriptions
            .Join(_context.Tenants, s => s.TenantId, t => t.Id, (s, t) => new { s, t })
            .GroupJoin(_context.Plans, x => x.s.PlanId, p => (Guid?)p.Id, (x, plans) => new { x.s, x.t, plans })
            .SelectMany(x => x.plans.DefaultIfEmpty(), (x, p) => new { x.s, x.t, p });

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            joined = joined.Where(x => x.t.Name.Contains(search) || x.t.Slug.Contains(search));
        }

        var totalCount = await joined.CountAsync(cancellationToken);

        var items = await joined
            .OrderByDescending(x => x.s.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new PlatformSubscriptionListItemDto(
                x.t.Id, x.t.Name, x.p == null ? null : x.p.Name, x.s.Status, x.s.CurrentPeriodEndUtc, x.s.Status == SubscriptionStatus.PastDue))
            .ToListAsync(cancellationToken);

        return new PagedResult<PlatformSubscriptionListItemDto>(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<PlatformPlanDto> CreatePlanAsync(CreatePlanRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var plan = new Plan
        {
            Code = request.Code.Trim().ToLowerInvariant(),
            Name = request.Name.Trim(),
            MaxUsers = request.MaxUsers,
            MaxMessagesPerMonth = request.MaxMessagesPerMonth,
            MaxCampaigns = request.MaxCampaigns,
            MaxKnowledgeBaseArticles = request.MaxKnowledgeBaseArticles,
            MaxLeadDiscoveryBatchSize = request.MaxLeadDiscoveryBatchSize ?? Plan.DefaultLeadDiscoveryBatchSize,
            PriceMonthlyCents = request.PriceMonthlyCents,
            StarterCredits = request.StarterCredits,
            MonthlyCredits = request.MonthlyCredits,
            IsActive = true
        };
        _context.Plans.Add(plan);

        // The plan's features are saved with it, so it is never sold with nothing to use.
        var features = (request.Features ?? Array.Empty<PlatformFeature>()).Distinct().ToList();
        foreach (var feature in features)
            _context.PlanFeatures.Add(new PlanFeature { PlanId = plan.Id, Feature = feature });

        var prices = new CountryPrices();
        foreach (var input in request.CountryPrices ?? Array.Empty<CountryPriceInput>())
        {
            if (input.Amount <= 0)
                continue;

            var code = input.CountryCode.ToUpperInvariant();
            _context.PlanPrices.Add(new PlanPrice { PlanId = plan.Id, CountryCode = code, Amount = input.Amount });
            prices[code] = input.Amount;
        }

        plan.PriceMonthlyCents = DeriveBaseCents(plan.PriceMonthlyCents, prices);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(plan, await _pricing.GetAsync(cancellationToken), features.Select(f => new PlanFeature { PlanId = plan.Id, Feature = f }), prices);
    }

    public async Task<PlatformPlanDto> UpdatePlanAsync(Guid id, UpdatePlanRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), id);

        plan.Name = request.Name.Trim();
        plan.MaxUsers = request.MaxUsers;
        plan.MaxMessagesPerMonth = request.MaxMessagesPerMonth;
        plan.MaxCampaigns = request.MaxCampaigns;
        plan.MaxKnowledgeBaseArticles = request.MaxKnowledgeBaseArticles;
        // Optional, so a client that predates the field leaves it alone rather than zeroing it.
        if (request.MaxLeadDiscoveryBatchSize is { } maxLeadDiscoveryBatchSize)
            plan.MaxLeadDiscoveryBatchSize = maxLeadDiscoveryBatchSize;
        plan.PriceMonthlyCents = request.PriceMonthlyCents;
        plan.IsActive = request.IsActive;
        if (request.StarterCredits is { } starter)
            plan.StarterCredits = starter;
        if (request.MonthlyCredits is { } monthly)
            plan.MonthlyCredits = monthly;

        var features = (await _context.PlanFeatures.Where(f => f.PlanId == id).ToListAsync(cancellationToken)).ToList();

        // Null leaves the features alone; a list is the complete new set. A tenant on the plan sees the change straight away.
        if (request.Features is { } wanted)
        {
            var wantedSet = wanted.ToHashSet();
            foreach (var row in features.Where(f => !wantedSet.Contains(f.Feature)).ToList())
            {
                _context.PlanFeatures.Remove(row);
                features.Remove(row);
            }

            foreach (var feature in wantedSet.Where(w => features.All(f => f.Feature != w)))
            {
                var row = new PlanFeature { PlanId = id, Feature = feature };
                _context.PlanFeatures.Add(row);
                features.Add(row);
            }
        }

        var existingPrices = await _context.PlanPrices.Where(p => p.PlanId == id).ToListAsync(cancellationToken);
        if (request.CountryPrices is { } wantedPrices)
        {
            foreach (var row in existingPrices.ToList())
            {
                var wantedPrice = wantedPrices.FirstOrDefault(w => string.Equals(w.CountryCode, row.CountryCode, StringComparison.OrdinalIgnoreCase));
                if (wantedPrice is null || wantedPrice.Amount <= 0)
                {
                    _context.PlanPrices.Remove(row);
                    existingPrices.Remove(row);
                }
                else
                {
                    row.Amount = wantedPrice.Amount;
                }
            }

            foreach (var input in wantedPrices.Where(w => w.Amount > 0))
            {
                if (existingPrices.Any(p => string.Equals(p.CountryCode, input.CountryCode, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var row = new PlanPrice { PlanId = id, CountryCode = input.CountryCode.ToUpperInvariant(), Amount = input.Amount };
                _context.PlanPrices.Add(row);
                existingPrices.Add(row);
            }
        }

        var prices = new CountryPrices();
        foreach (var row in existingPrices)
            prices[row.CountryCode] = row.Amount;

        plan.PriceMonthlyCents = DeriveBaseCents(plan.PriceMonthlyCents, prices);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(plan, await _pricing.GetAsync(cancellationToken), features, prices);
    }

    public async Task DeactivatePlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), id);

        if (!plan.IsActive)
            return;

        plan.IsActive = false;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlatformCreditPackDto>> GetCreditPacksAsync(CancellationToken cancellationToken = default)
    {
        var pricing = await _pricing.GetAsync(cancellationToken);
        var packs = await _context.CreditPacks.ToListAsync(cancellationToken);
        var pricesByPack = await CatalogPricing.LoadPackPricesAsync(_context, packs.Select(p => p.Id).ToList(), cancellationToken);

        return packs
            .OrderBy(p => p.QuotaType).ThenBy(p => p.Units)
            .Select(p => ToDto(p, pricing, pricesByPack.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public async Task<PlatformCreditPackDto> CreateCreditPackAsync(CreateCreditPackRequest request, CancellationToken cancellationToken = default)
    {
        await _createPackValidator.ValidateAndThrowAsync(request, cancellationToken);

        var pack = new CreditPack
        {
            QuotaType = QuotaType.Credits, // there is one wallet: every pack is credits
            Name = request.Name.Trim(),
            Units = request.Units,
            PriceCents = request.PriceCents,
            IsActive = true
        };
        _context.CreditPacks.Add(pack);

        var prices = new CountryPrices();
        foreach (var input in request.CountryPrices ?? Array.Empty<CountryPriceInput>())
        {
            if (input.Amount <= 0)
                continue;

            var code = input.CountryCode.ToUpperInvariant();
            _context.CreditPackPrices.Add(new CreditPackPrice { CreditPackId = pack.Id, CountryCode = code, Amount = input.Amount });
            prices[code] = input.Amount;
        }

        pack.PriceCents = DeriveBaseCents(pack.PriceCents, prices);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(pack, await _pricing.GetAsync(cancellationToken), prices);
    }

    public async Task<PlatformCreditPackDto> UpdateCreditPackAsync(Guid id, UpdateCreditPackRequest request, CancellationToken cancellationToken = default)
    {
        await _updatePackValidator.ValidateAndThrowAsync(request, cancellationToken);

        var pack = await _context.CreditPacks.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(CreditPack), id);

        pack.Name = request.Name.Trim();
        pack.Units = request.Units;
        pack.PriceCents = request.PriceCents;
        pack.IsActive = request.IsActive;

        var existingPrices = await _context.CreditPackPrices.Where(p => p.CreditPackId == id).ToListAsync(cancellationToken);
        if (request.CountryPrices is { } wantedPrices)
        {
            foreach (var row in existingPrices.ToList())
            {
                var wantedPrice = wantedPrices.FirstOrDefault(w => string.Equals(w.CountryCode, row.CountryCode, StringComparison.OrdinalIgnoreCase));
                if (wantedPrice is null || wantedPrice.Amount <= 0)
                {
                    _context.CreditPackPrices.Remove(row);
                    existingPrices.Remove(row);
                }
                else
                {
                    row.Amount = wantedPrice.Amount;
                }
            }

            foreach (var input in wantedPrices.Where(w => w.Amount > 0))
            {
                if (existingPrices.Any(p => string.Equals(p.CountryCode, input.CountryCode, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var row = new CreditPackPrice { CreditPackId = id, CountryCode = input.CountryCode.ToUpperInvariant(), Amount = input.Amount };
                _context.CreditPackPrices.Add(row);
                existingPrices.Add(row);
            }
        }

        var prices = new CountryPrices();
        foreach (var row in existingPrices)
            prices[row.CountryCode] = row.Amount;

        pack.PriceCents = DeriveBaseCents(pack.PriceCents, prices);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(pack, await _pricing.GetAsync(cancellationToken), prices);
    }

    public async Task DeactivateCreditPackAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var pack = await _context.CreditPacks.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(CreditPack), id);

        if (!pack.IsActive)
            return;

        pack.IsActive = false;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, List<PlanFeature>>> LoadFeaturesAsync(IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken) =>
        (await _context.PlanFeatures.Where(f => planIds.Contains(f.PlanId)).ToListAsync(cancellationToken))
            .GroupBy(f => f.PlanId)
            .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>The USD-cents figure older code still reads (list ordering, the legacy Payment column) - taken from India's
    /// price, else the first country price set, else left as it was. Prices themselves live per country; nothing is sold from this.</summary>
    private static int DeriveBaseCents(int current, CountryPrices prices)
    {
        if (prices.Count == 0)
            return current;

        var code = prices.ContainsKey("IN") ? "IN" : prices.Keys.OrderBy(k => k).First();
        var region = RegionalPricingCatalog.Resolve(code);
        return (int)Math.Round(prices[code] / region.RateToUsd * 100m, MidpointRounding.AwayFromZero);
    }

    private static IReadOnlyList<CountryPriceDto> ToCountryPriceDtos(CountryPrices? prices) =>
        (prices ?? new CountryPrices())
            .Select(kv => (Pricing: RegionalPricingCatalog.All.FirstOrDefault(r => string.Equals(r.CountryCode, kv.Key, StringComparison.OrdinalIgnoreCase)), kv.Value))
            .Where(x => x.Pricing is not null)
            .OrderBy(x => x.Pricing!.CountryName)
            .Select(x => new CountryPriceDto(x.Pricing!.CountryCode, x.Pricing.CountryName, x.Pricing.CurrencyCode, x.Pricing.CurrencySymbol, x.Value))
            .ToList();

    private static PlatformPlanDto ToDto(Plan p, RegionalPricing pricing, IEnumerable<PlanFeature>? features, CountryPrices? prices) => new(
        p.Id, p.Code, p.Name, p.MaxUsers, p.MaxMessagesPerMonth,
        p.MaxCampaigns, p.MaxKnowledgeBaseArticles, p.MaxLeadDiscoveryBatchSize, p.PriceMonthlyCents, p.IsActive,
        pricing.CurrencyCode,
        pricing.CurrencySymbol,
        // The operator's own country's price: an explicit one if set, else the base converted. Two decimals - a
        // plan price is a real amount someone pays, not a fraction-of-a-cent estimate.
        prices?.GetValueOrDefault(pricing.CountryCode) ?? 0m,
        p.StarterCredits,
        p.MonthlyCredits,
        (features ?? Array.Empty<PlanFeature>()).Select(f => f.Feature).OrderBy(f => f).ToList(),
        ToCountryPriceDtos(prices));

    private static PlatformCreditPackDto ToDto(CreditPack p, RegionalPricing pricing, CountryPrices? prices) => new(
        p.Id, p.QuotaType, p.Name, p.Units, p.PriceCents, p.IsActive,
        pricing.CurrencyCode,
        pricing.CurrencySymbol,
        prices?.GetValueOrDefault(pricing.CountryCode) ?? 0m,
        ToCountryPriceDtos(prices));
}
