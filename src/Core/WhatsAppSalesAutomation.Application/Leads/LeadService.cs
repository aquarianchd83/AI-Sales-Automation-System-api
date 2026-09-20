using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

public class LeadService : ILeadService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly ILeadScoringService _scoring;
    private readonly IValidator<UpdateLeadRequest> _updateValidator;
    private readonly IValidator<AddLeadActivityRequest> _activityValidator;

    public LeadService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        ILeadScoringService scoring,
        IValidator<UpdateLeadRequest> updateValidator,
        IValidator<AddLeadActivityRequest> activityValidator)
    {
        _context = context;
        _dateTime = dateTime;
        _scoring = scoring;
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

        // A value an agent typed is as real as one the AI extracted, so it is captured the same way and
        // earns the same field weight. Confidence 1.0: a human is not guessing.
        // Flushed before scoring: RecomputeAsync reads captured values back from the database, and EF
        // does not flush pending inserts before a query - without this the value just entered would
        // earn nothing until something else rescored the lead.
        if (await CaptureMirroredValuesAsync(
                lead, request.Budget, request.Interest, request.PurchaseTimeline,
                confidence: 1.0, capturedByUserId: updatedByUserId, capturedFromMessageId: null, cancellationToken))
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        await RescoreAsync(lead, new ScoringSignals(DetectedIntent: lead.CurrentIntent), cancellationToken);

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

    public async Task<PagedResult<LeadActivityDto>> GetActivitiesAsync(Guid id, PagedRequest request, CancellationToken cancellationToken = default)
    {
        var exists = await _context.Leads.AnyAsync(l => l.Id == id, cancellationToken);
        if (!exists)
            throw new NotFoundException(nameof(Lead), id);

        var query = _context.LeadActivities.Where(a => a.LeadId == id);

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<LeadActivityDto>(rows.Select(a => a.ToDto()).ToList(), totalCount, request.Page, request.PageSize);
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

    /// <summary>
    /// Recomputes the score from the tenant's configured rules and field weights, and records a
    /// LeadActivity when it actually moves.
    ///
    /// The activity row is written here rather than inside the scoring service because it is a lead
    /// history concern, and because only this layer knows whether the change came from an agent or
    /// from an AI turn - the scoring service is given signals, not an actor.
    /// </summary>
    private async Task RescoreAsync(Lead lead, ScoringSignals signals, CancellationToken cancellationToken)
    {
        var previous = lead.ScoreNumeric;

        var result = await _scoring.RecomputeAsync(lead.Id, signals, cancellationToken);

        if (result.ScoreNumeric != previous)
            AddActivity(lead, LeadActivityType.ScoreChanged, previous.ToString(), result.ScoreNumeric.ToString(), null, null);
    }

    /// <summary>
    /// Writes the three well-known qualification values that mirror Lead.Budget/Interest/
    /// PurchaseTimeline, superseding any current value for the same field.
    ///
    /// A null argument means "this turn said nothing about it" and leaves the existing value alone -
    /// the same merge-don't-erase rule the columns themselves follow. A value the field's own type
    /// rejects is skipped rather than stored: the column keeps it (so nothing regresses relative to
    /// the old behaviour) but it earns no points, which is the honest outcome for a value we could not
    /// make sense of.
    /// </summary>
    private async Task<bool> CaptureMirroredValuesAsync(
        Lead lead,
        string? budget,
        string? interest,
        string? purchaseTimeline,
        double confidence,
        Guid? capturedByUserId,
        Guid? capturedFromMessageId,
        CancellationToken cancellationToken)
    {
        var incoming = new (string Key, string? Value)[]
        {
            (QualificationDefaults.BudgetKey, budget),
            (QualificationDefaults.InterestKey, interest),
            (QualificationDefaults.PurchaseTimelineKey, purchaseTimeline)
        }
        .Where(x => !string.IsNullOrWhiteSpace(x.Value))
        .ToList();

        if (incoming.Count == 0)
            return false;

        var keys = incoming.Select(x => x.Key).ToList();

        var fields = await _context.QualificationFields
            .Where(f => f.IsActive && keys.Contains(f.FieldKey))
            .ToListAsync(cancellationToken);

        if (fields.Count == 0)
            return false;

        var captured = false;

        var current = await _context.LeadQualificationValues
            .Where(v => v.LeadId == lead.Id && !v.IsSuperseded && keys.Contains(v.FieldKey))
            .ToListAsync(cancellationToken);

        foreach (var (key, value) in incoming)
        {
            var field = fields.FirstOrDefault(f => string.Equals(f.FieldKey, key, StringComparison.OrdinalIgnoreCase));
            if (field is null)
                continue;

            var normalized = QualificationValueNormalizer.Normalize(field, value!);
            if (!normalized.IsAccepted)
                continue;

            var existing = current.FirstOrDefault(v => string.Equals(v.FieldKey, key, StringComparison.OrdinalIgnoreCase));

            // Unchanged answers are left alone. Superseding a row with an identical one would churn
            // the capture history and make "the customer changed their mind" unreadable.
            if (existing is not null && string.Equals(existing.RawValue, normalized.RawValue, StringComparison.Ordinal))
                continue;

            if (existing is not null)
                existing.IsSuperseded = true;

            _context.LeadQualificationValues.Add(new LeadQualificationValue
            {
                TenantId = lead.TenantId,
                LeadId = lead.Id,
                FieldId = field.Id,
                FieldKey = field.FieldKey,
                RawValue = normalized.RawValue,
                NormalizedValue = normalized.NormalizedValue,
                CapturedFromMessageId = capturedFromMessageId,
                CapturedByUserId = capturedByUserId,
                ExtractionConfidence = confidence
            });

            captured = true;
        }

        return captured;
    }

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
