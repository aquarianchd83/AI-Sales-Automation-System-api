using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

public class LeadService : ILeadService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<UpdateLeadRequest> _updateValidator;
    private readonly IValidator<AddLeadActivityRequest> _activityValidator;

    public LeadService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IValidator<UpdateLeadRequest> updateValidator,
        IValidator<AddLeadActivityRequest> activityValidator)
    {
        _context = context;
        _dateTime = dateTime;
        _updateValidator = updateValidator;
        _activityValidator = activityValidator;
    }

    public async Task<PagedResult<LeadDto>> GetPagedAsync(PagedRequest request, string? stage = null, string? score = null, CancellationToken cancellationToken = default)
    {
        // Anonymous-type projection, not a custom record - EF Core can translate further Where/OrderBy
        // on this freely. A custom record here would break exactly like HandoffService's did before
        // its fix: see that class's BaseQuery doc comment for the full explanation.
        var query =
            from l in _context.Leads
            join cust in _context.Customers on l.CustomerId equals cust.Id
            select new { Lead = l, Customer = cust };

        if (!string.IsNullOrWhiteSpace(stage))
        {
            if (!Enum.TryParse<LeadStage>(stage, ignoreCase: true, out var parsedStage))
                throw Invalid("stage", $"Stage must be one of: {string.Join(", ", Enum.GetNames<LeadStage>())}.");

            query = query.Where(x => x.Lead.Stage == parsedStage);
        }

        if (!string.IsNullOrWhiteSpace(score))
        {
            if (!Enum.TryParse<LeadScoreBand>(score, ignoreCase: true, out var parsedScore))
                throw Invalid("score", $"Score must be one of: {string.Join(", ", Enum.GetNames<LeadScoreBand>())}.");

            query = query.Where(x => x.Lead.Score == parsedScore);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x =>
                x.Customer.PhoneNumberE164.Contains(search) ||
                (x.Customer.FirstName != null && x.Customer.FirstName.Contains(search)) ||
                (x.Customer.LastName != null && x.Customer.LastName.Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.Lead.ScoreNumeric)
            .ThenByDescending(x => x.Lead.LastActivityAt ?? x.Lead.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(x => x.Lead.ToDto(x.Customer)).ToList();

        return new PagedResult<LeadDto>(items, totalCount, request.Page, request.PageSize);
    }

    public async Task<LeadDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await (
                from l in _context.Leads
                join cust in _context.Customers on l.CustomerId equals cust.Id
                where l.Id == id
                select new { Lead = l, Customer = cust })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Lead), id);

        return row.Lead.ToDto(row.Customer);
    }

    public async Task<LeadDto> UpdateAsync(Guid id, UpdateLeadRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var lead = await FindOrThrowAsync(id, cancellationToken);
        var now = _dateTime.UtcNow;

        if (request.Stage is not null)
        {
            var newStage = Enum.Parse<LeadStage>(request.Stage, ignoreCase: true);
            if (newStage != lead.Stage)
            {
                AddActivity(lead, LeadActivityType.StageChanged, lead.Stage.ToString(), newStage.ToString(), null, updatedByUserId);
                lead.Stage = newStage;
            }
        }

        RecordFieldChangeIfDifferent(lead, "Budget", lead.Budget, request.Budget, updatedByUserId);
        RecordFieldChangeIfDifferent(lead, "Interest", lead.Interest, request.Interest, updatedByUserId);
        RecordFieldChangeIfDifferent(lead, "PurchaseTimeline", lead.PurchaseTimeline, request.PurchaseTimeline, updatedByUserId);

        if (request.Budget is not null) lead.Budget = request.Budget;
        if (request.Interest is not null) lead.Interest = request.Interest;
        if (request.PurchaseTimeline is not null) lead.PurchaseTimeline = request.PurchaseTimeline;

        lead.LastActivityAt = now;
        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<LeadDto> AssignAsync(Guid id, AssignLeadRequest request, CancellationToken cancellationToken = default)
    {
        var lead = await FindOrThrowAsync(id, cancellationToken);
        var now = _dateTime.UtcNow;

        AddActivity(lead, LeadActivityType.AssignmentChanged, lead.AssignedTo?.ToString(), request.AgentId.ToString(), null, request.AgentId);
        lead.AssignedTo = request.AgentId;
        lead.LastActivityAt = now;

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<LeadActivityDto> AddActivityAsync(Guid id, AddLeadActivityRequest request, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        await _activityValidator.ValidateAndThrowAsync(request, cancellationToken);

        var lead = await FindOrThrowAsync(id, cancellationToken);
        var now = _dateTime.UtcNow;

        var activity = AddActivity(lead, LeadActivityType.Note, null, null, request.Note.Trim(), createdByUserId);
        lead.LastActivityAt = now;

        await _context.SaveChangesAsync(cancellationToken);

        return activity.ToDto();
    }

    public async Task<Guid> GetOrCreateActiveLeadIdAsync(Guid customerId, Guid? campaignId, CancellationToken cancellationToken = default)
    {
        var existingId = await _context.Leads
            .Where(l => l.CustomerId == customerId && l.Stage != LeadStage.Won && l.Stage != LeadStage.Lost)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingId.HasValue)
            return existingId.Value;

        var lead = new Lead { CustomerId = customerId, CampaignId = campaignId };
        _context.Leads.Add(lead);
        await _context.SaveChangesAsync(cancellationToken);

        return lead.Id;
    }

    public async Task<LeadDto> ApplyAiExtractedAttributesAsync(Guid leadId, AiExtractedEntities entities, string? detectedIntent, CancellationToken cancellationToken = default)
    {
        var lead = await FindOrThrowAsync(leadId, cancellationToken);
        var now = _dateTime.UtcNow;

        // Merge only - a later turn not mentioning budget again must not erase an earlier answer.
        // CreatedBy null marks these as system/AI-driven, distinct from an agent's manual UpdateAsync.
        RecordFieldChangeIfDifferent(lead, "Budget", lead.Budget, entities.Budget, null);
        RecordFieldChangeIfDifferent(lead, "Interest", lead.Interest, entities.Interest, null);
        RecordFieldChangeIfDifferent(lead, "PurchaseTimeline", lead.PurchaseTimeline, entities.PurchaseTimeline, null);

        if (!string.IsNullOrWhiteSpace(entities.Budget)) lead.Budget = entities.Budget;
        if (!string.IsNullOrWhiteSpace(entities.Interest)) lead.Interest = entities.Interest;
        if (!string.IsNullOrWhiteSpace(entities.PurchaseTimeline)) lead.PurchaseTimeline = entities.PurchaseTimeline;

        var newScoreNumeric = ComputeScoreNumeric(lead, detectedIntent);
        if (newScoreNumeric != lead.ScoreNumeric)
        {
            AddActivity(lead, LeadActivityType.ScoreChanged, lead.ScoreNumeric.ToString(), newScoreNumeric.ToString(), null, null);
            lead.ScoreNumeric = newScoreNumeric;
            lead.Score = BandFor(newScoreNumeric);
        }

        // New == Qualifying as soon as the AI has extracted anything at all - "we're talking about
        // requirements now", not yet "Qualified" which stays a human/business judgement call rather
        // than something the AI decides unilaterally.
        if (lead.Stage == LeadStage.New && (lead.Budget is not null || lead.Interest is not null || lead.PurchaseTimeline is not null))
        {
            AddActivity(lead, LeadActivityType.StageChanged, lead.Stage.ToString(), LeadStage.Qualifying.ToString(), null, null);
            lead.Stage = LeadStage.Qualifying;
        }

        lead.LastActivityAt = now;
        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(leadId, cancellationToken);
    }

    /// <summary>
    /// Deliberately simple, fully deterministic first-pass heuristic rather than a model-scored one:
    /// +30 for each of Budget/Interest/PurchaseTimeline being known (completeness signals genuine
    /// qualification progress), +/-20 for an intent that itself signals strong buying interest
    /// (Negotiation) or a reason to distrust the signal (Complaint). Capped to keep ScoreNumeric a
    /// stable 0-100 scale regardless of how many turns a conversation has had. Revisit if/when real
    /// usage data suggests better weights.
    /// </summary>
    private static int ComputeScoreNumeric(Lead lead, string? detectedIntent)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(lead.Budget)) score += 30;
        if (!string.IsNullOrWhiteSpace(lead.Interest)) score += 30;
        if (!string.IsNullOrWhiteSpace(lead.PurchaseTimeline)) score += 20;

        if (string.Equals(detectedIntent, "Negotiation", StringComparison.OrdinalIgnoreCase)) score += 20;
        else if (string.Equals(detectedIntent, "Complaint", StringComparison.OrdinalIgnoreCase)) score -= 20;

        return Math.Clamp(score, 0, 100);
    }

    private static LeadScoreBand BandFor(int scoreNumeric) => scoreNumeric switch
    {
        >= 70 => LeadScoreBand.Hot,
        >= 40 => LeadScoreBand.Warm,
        _ => LeadScoreBand.Cold
    };

    private void RecordFieldChangeIfDifferent(Lead lead, string fieldName, string? oldValue, string? newValue, Guid? createdBy)
    {
        if (newValue is null || newValue == oldValue)
            return;

        AddActivity(lead, LeadActivityType.Note, oldValue, newValue, $"{fieldName} updated", createdBy);
    }

    private LeadActivity AddActivity(Lead lead, LeadActivityType type, string? oldValue, string? newValue, string? note, Guid? createdBy)
    {
        var activity = new LeadActivity
        {
            LeadId = lead.Id,
            ActivityType = type,
            OldValue = oldValue,
            NewValue = newValue,
            Note = note,
            CreatedBy = createdBy
        };
        _context.LeadActivities.Add(activity);
        return activity;
    }

    private async Task<Lead> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.Leads.FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Lead), id);

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
