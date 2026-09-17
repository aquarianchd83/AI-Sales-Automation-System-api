using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

public class LeadDiscoveryService : ILeadDiscoveryService
{
    /// <summary>Every stored lead passed qualification - rejected candidates are never saved.</summary>
    private const string QualifiedStatus = "Qualified";

    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly IPlanLimitsService _planLimits;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<SaveLeadDiscoveryProfileRequest> _saveValidator;

    public LeadDiscoveryService(
        IApplicationDbContext context,
        ITenantContext tenantContext,
        IPlanLimitsService planLimits,
        IDateTimeProvider dateTime,
        IValidator<SaveLeadDiscoveryProfileRequest> saveValidator)
    {
        _context = context;
        _tenantContext = tenantContext;
        _planLimits = planLimits;
        _dateTime = dateTime;
        _saveValidator = saveValidator;
    }

    public async Task<LeadDiscoveryProfileDto> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId();
        var profile = await _context.LeadDiscoveryProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var planLimit = await _planLimits.GetLeadDiscoveryBatchLimitAsync(tenantId, cancellationToken);

        return ToDto(profile ?? new LeadDiscoveryProfile(), planLimit);
    }

    public async Task<LeadDiscoveryProfileDto> SaveProfileAsync(SaveLeadDiscoveryProfileRequest request, CancellationToken cancellationToken = default)
    {
        await _saveValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tenantId = CurrentTenantId();
        var planLimit = await _planLimits.GetLeadDiscoveryBatchLimitAsync(tenantId, cancellationToken);
        if (request.BatchSize > planLimit)
            throw new PlanLimitExceededException($"Your plan allows up to {planLimit} discovered leads per run. Upgrade to raise the batch size.");

        var profile = await _context.LeadDiscoveryProfiles.FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            profile = new LeadDiscoveryProfile { TenantId = tenantId };
            _context.LeadDiscoveryProfiles.Add(profile);
        }

        profile.IsEnabled = request.IsEnabled;
        profile.TargetBusinessType = request.TargetBusinessType.Trim();
        profile.Keywords = TenantBusinessDetails.NormalizeKeywords(request.Keywords);
        profile.Locations = TenantBusinessDetails.NormalizeKeywords(request.Locations);
        profile.BatchSize = request.BatchSize;
        profile.RequiredFields = (request.RequiredFields ?? Array.Empty<string>())
            .Select(LeadDiscoveryFields.Canonical)
            .OfType<string>()
            .Distinct()
            .ToList();
        profile.PhoneRequired = request.PhoneRequired;
        profile.EmailRequired = request.EmailRequired;
        profile.IndependentBusiness = request.IndependentBusiness;
        profile.MinimumLeadScore = request.MinimumLeadScore;
        profile.AdditionalCriteria = TenantBusinessDetails.NormalizeKeywords(request.AdditionalCriteria);

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(profile, planLimit);
    }

    public async Task<PagedResult<DiscoveredLeadDto>> GetDiscoveredLeadsAsync(PagedRequest request, int? minScore = null, CancellationToken cancellationToken = default)
    {
        var query = _context.DiscoveredLeads.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(l => l.BusinessName.Contains(search) || l.BusinessType.Contains(search) || (l.City != null && l.City.Contains(search)));
        }

        if (minScore is { } score)
            query = query.Where(l => l.LeadScore >= score);

        var totalCount = await query.CountAsync(cancellationToken);

        var leads = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<DiscoveredLeadDto>(leads.Select(ToDto).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<PagedResult<LeadDiscoveryRunDto>> GetRunsAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var pricing = await ResolvePricingAsync(cancellationToken);
        var query = _context.LeadDiscoveryRuns.AsNoTracking();

        var totalCount = await query.CountAsync(cancellationToken);

        var runs = await query
            .OrderByDescending(r => r.RanAtUtc)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<LeadDiscoveryRunDto>(
            runs.Select(r => ToDto(r, pricing)).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<LeadDiscoverySpendDto> GetSpendAsync(CancellationToken cancellationToken = default)
    {
        var pricing = await ResolvePricingAsync(cancellationToken);
        var runs = _context.LeadDiscoveryRuns.AsNoTracking();

        // Start of the current UTC calendar month - the same "resets on the 1st" definition
        // PlanLimitsService uses for the monthly message allowance, so the two read alike.
        var now = _dateTime.UtcNow;
        var monthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var currentMonth = await SummariseAsync(runs.Where(r => r.RanAtUtc >= monthStartUtc), pricing, cancellationToken);
        var allTime = await SummariseAsync(runs, pricing, cancellationToken);

        var lastRunAtUtc = allTime.Runs == 0 ? (DateTime?)null : await runs.MaxAsync(r => r.RanAtUtc, cancellationToken);

        return new LeadDiscoverySpendDto(pricing.CurrencyCode, pricing.CurrencySymbol, currentMonth, allTime, lastRunAtUtc);
    }

    /// <summary>Aggregated with scalar queries rather than one grouped projection - see this codebase's
    /// EF Core note about operators chained after a projection.</summary>
    private static async Task<LeadDiscoverySpendPeriodDto> SummariseAsync(
        IQueryable<LeadDiscoveryRun> runs, RegionalPricing pricing, CancellationToken cancellationToken)
    {
        var count = await runs.CountAsync(cancellationToken);
        if (count == 0)
            return new LeadDiscoverySpendPeriodDto(0, 0, 0m, 0m, 0m);

        var leads = await runs.SumAsync(r => r.LeadsSaved, cancellationToken);
        var usd = await runs.SumAsync(r => r.EstimatedCostUsd, cancellationToken);

        return new LeadDiscoverySpendPeriodDto(
            count,
            leads,
            usd,
            ToLocal(usd, pricing),
            leads == 0 ? 0m : Math.Round(usd / leads, 6, MidpointRounding.AwayFromZero));
    }

    /// <summary>The tenant's own currency, from its Country - falls back to USD like every other price in
    /// this application (see RegionalPricingCatalog.Resolve).</summary>
    private async Task<RegionalPricing> ResolvePricingAsync(CancellationToken cancellationToken)
    {
        var tenantId = CurrentTenantId();

        var countryCode = await _context.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);

        return RegionalPricingCatalog.Resolve(countryCode);
    }

    private static decimal ToLocal(decimal usd, RegionalPricing pricing) =>
        Math.Round(usd * pricing.RateToUsd, 2, MidpointRounding.AwayFromZero);

    private Guid CurrentTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Lead discovery requires a tenant in scope.");

    private static LeadDiscoveryProfileDto ToDto(LeadDiscoveryProfile p, int? planLimit) => new(
        p.IsEnabled, p.TargetBusinessType, p.Keywords, p.Locations, p.BatchSize, planLimit, p.RequiredFields,
        p.PhoneRequired, p.EmailRequired, p.IndependentBusiness, p.MinimumLeadScore, p.AdditionalCriteria,
        p.UpdatedAt ?? (p.CreatedAt == default ? null : p.CreatedAt));

    private static DiscoveredLeadDto ToDto(DiscoveredLead l) => new(
        l.Id, l.BusinessName, l.BusinessType, l.ContactPerson, l.Address, l.City, l.State, l.Phone, l.Email,
        l.Website, l.SourceUrl, l.PhoneVerified, l.PhoneSourceUrl, QualifiedStatus, l.LeadScore, l.ScoreRationale,
        l.CustomerId, l.CreatedAt);

    private static LeadDiscoveryRunDto ToDto(LeadDiscoveryRun r, RegionalPricing pricing) => new(
        r.Id, r.RanAtUtc, r.Model, r.Rounds, r.CandidatesConsidered, r.LeadsSaved, r.Duplicates, r.Rejected,
        r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.WebSearches, r.WebFetches,
        r.EstimatedCostUsd, ToLocal(r.EstimatedCostUsd, pricing));
}
