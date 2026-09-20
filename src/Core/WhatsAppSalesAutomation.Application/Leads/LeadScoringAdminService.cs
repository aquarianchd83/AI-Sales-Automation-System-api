using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

public class LeadScoringAdminService : ILeadScoringAdminService
{
    private readonly IApplicationDbContext _context;
    private readonly IValidator<CreateLeadScoringRuleRequest> _createValidator;
    private readonly IValidator<UpdateLeadScoringRuleRequest> _updateValidator;

    public LeadScoringAdminService(
        IApplicationDbContext context,
        IValidator<CreateLeadScoringRuleRequest> createValidator,
        IValidator<UpdateLeadScoringRuleRequest> updateValidator)
    {
        _context = context;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<IReadOnlyList<LeadScoringRuleDto>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        var rules = await _context.LeadScoringRules
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.RuleKey)
            .ToListAsync(cancellationToken);

        return rules.Select(ToDto).ToList();
    }

    public async Task<LeadScoringRuleDto> GetRuleAsync(Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await FindOrThrowAsync(id, cancellationToken));

    public async Task<LeadScoringRuleDto> CreateRuleAsync(
        CreateLeadScoringRuleRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var key = request.RuleKey.Trim().ToLowerInvariant();

        if (await _context.LeadScoringRules.AnyAsync(r => r.RuleKey == key, cancellationToken))
            throw new ConflictException($"A scoring rule with key '{key}' already exists.");

        var count = await _context.LeadScoringRules.CountAsync(cancellationToken);
        if (count >= LeadScoringLimits.MaxRulesPerTenant)
        {
            throw Invalid("ruleKey",
                $"A tenant can have at most {LeadScoringLimits.MaxRulesPerTenant} scoring rules. " +
                "Deactivate or remove one first.");
        }

        var rule = new LeadScoringRule
        {
            RuleKey = key,
            DisplayName = request.DisplayName.Trim(),
            RuleType = Enum.Parse<LeadScoringRuleType>(request.RuleType, ignoreCase: true),
            MatchValue = NormalizeMatchValue(request.RuleType, request.MatchValue),
            Points = request.Points,
            OncePerLead = request.OncePerLead,
            MarksLeadHot = request.MarksLeadHot,
            IsActive = true,
            SortOrder = request.SortOrder,
            LastUpdatedBy = updatedByUserId
        };

        _context.LeadScoringRules.Add(rule);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(rule);
    }

    public async Task<LeadScoringRuleDto> UpdateRuleAsync(
        Guid id, UpdateLeadScoringRuleRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var rule = await FindOrThrowAsync(id, cancellationToken);

        rule.DisplayName = request.DisplayName.Trim();
        rule.RuleType = Enum.Parse<LeadScoringRuleType>(request.RuleType, ignoreCase: true);
        rule.MatchValue = NormalizeMatchValue(request.RuleType, request.MatchValue);
        rule.Points = request.Points;
        rule.OncePerLead = request.OncePerLead;
        rule.MarksLeadHot = request.MarksLeadHot;
        rule.IsActive = request.IsActive;
        rule.SortOrder = request.SortOrder;
        rule.LastUpdatedBy = updatedByUserId;

        await _context.SaveChangesAsync(cancellationToken);

        // Existing leads keep the score they already have. Recomputing every lead on a rule edit would
        // rewrite history the sales team has been working from, and the score is a running tally of
        // what happened turn by turn, not a formula re-evaluated over the whole conversation.
        return ToDto(rule);
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rule = await FindOrThrowAsync(id, cancellationToken);

        _context.LeadScoringRules.Remove(rule);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LeadScoringRuleDto>> SeedDefaultsAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters + explicit TenantId: called from signup and from the backfill, where the
        // ambient tenant is not necessarily the one being seeded.
        var existingKeys = await _context.LeadScoringRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.RuleKey)
            .ToListAsync(cancellationToken);

        var existing = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);

        foreach (var seed in QualificationDefaults.ScoringRules)
        {
            if (existing.Contains(seed.RuleKey))
                continue;

            _context.LeadScoringRules.Add(new LeadScoringRule
            {
                TenantId = tenantId,
                RuleKey = seed.RuleKey,
                DisplayName = seed.DisplayName,
                RuleType = seed.RuleType,
                MatchValue = seed.MatchValue,
                Points = seed.Points,
                OncePerLead = seed.OncePerLead,
                MarksLeadHot = seed.MarksLeadHot,
                IsActive = true,
                SortOrder = seed.SortOrder
            });
        }

        await _context.SaveChangesAsync(cancellationToken);

        var seeded = await _context.LeadScoringRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.RuleKey)
            .ToListAsync(cancellationToken);

        return seeded.Select(ToDto).ToList();
    }

    public async Task<LeadScoringCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var fieldKeys = await _context.QualificationFields
            .Where(f => f.IsActive)
            .OrderBy(f => f.FieldKey)
            .Select(f => f.FieldKey)
            .ToListAsync(cancellationToken);

        var ruleTypes = new List<LeadScoringRuleTypeDto>
        {
            new(nameof(LeadScoringRuleType.IntentMatch),
                "A customer intent name", "DemoRequest"),
            new(nameof(LeadScoringRuleType.FieldPresent),
                "A qualification field key", fieldKeys.FirstOrDefault() ?? "budget"),
            new(nameof(LeadScoringRuleType.FieldValueMatch),
                "field_key=value", "property_type=3BHK"),
            new(nameof(LeadScoringRuleType.TimelineWithinDays),
                "A number of days", "30"),
            new(nameof(LeadScoringRuleType.MessageKeyword),
                "A word to look for in the customer's message", "quotation"),
            new(nameof(LeadScoringRuleType.BuyingIntentDetected),
                "Not used - this rule fires on the AI's buying-intent signal", string.Empty)
        };

        return new LeadScoringCatalogDto(ruleTypes, Enum.GetNames<CustomerIntent>(), fieldKeys);
    }

    public async Task<LeadScoreBreakdownDto> GetBreakdownAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken)
            ?? throw new NotFoundException("Lead", leadId);

        var contributions = await _context.LeadScoreContributions
            .Where(c => c.LeadId == leadId)
            .OrderByDescending(c => c.AppliedAt)
            .ToListAsync(cancellationToken);

        return new LeadScoreBreakdownDto(
            leadId,
            lead.ScoreNumeric,
            contributions.Sum(c => c.Points),
            lead.Score.ToString(),
            lead.HotLeadDetectedAt is not null,
            lead.HotLeadReason,
            lead.HotLeadDetectedAt,
            contributions
                .Select(c => new LeadScoreContributionDto(c.SourceKey, c.DisplayName, c.Points, c.AppliedAt))
                .ToList());
    }

    /// <summary>BuyingIntentDetected ignores MatchValue, so an empty string is stored rather than
    /// whatever the UI happened to leave in the box - otherwise two rules that behave identically
    /// would look different in the list.</summary>
    private static string NormalizeMatchValue(string ruleType, string matchValue)
    {
        var value = (matchValue ?? string.Empty).Trim();

        return Enum.TryParse<LeadScoringRuleType>(ruleType, ignoreCase: true, out var parsed)
            && parsed == LeadScoringRuleType.BuyingIntentDetected
            ? string.Empty
            : value;
    }

    private static LeadScoringRuleDto ToDto(LeadScoringRule r) => new(
        r.Id, r.RuleKey, r.DisplayName, r.RuleType.ToString(), r.MatchValue,
        r.Points, r.OncePerLead, r.MarksLeadHot, r.IsActive, r.SortOrder, r.CreatedAt, r.UpdatedAt);

    private async Task<LeadScoringRule> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.LeadScoringRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
        ?? throw new NotFoundException("LeadScoringRule", id);

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
