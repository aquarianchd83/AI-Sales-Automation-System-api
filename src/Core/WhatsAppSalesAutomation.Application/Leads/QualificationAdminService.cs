using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

public class QualificationAdminService : IQualificationAdminService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<CreateQualificationFieldRequest> _createValidator;
    private readonly IValidator<UpdateQualificationFieldRequest> _updateValidator;
    private readonly IValidator<ReorderQualificationFieldsRequest> _reorderValidator;
    private readonly IValidator<SetLeadQualificationValueRequest> _setValueValidator;

    public QualificationAdminService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IValidator<CreateQualificationFieldRequest> createValidator,
        IValidator<UpdateQualificationFieldRequest> updateValidator,
        IValidator<ReorderQualificationFieldsRequest> reorderValidator,
        IValidator<SetLeadQualificationValueRequest> setValueValidator)
    {
        _context = context;
        _dateTime = dateTime;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _reorderValidator = reorderValidator;
        _setValueValidator = setValueValidator;
    }

    public async Task<IReadOnlyList<QualificationFieldDto>> GetFieldsAsync(CancellationToken cancellationToken = default)
    {
        var fields = await OrderedFields().ToListAsync(cancellationToken);
        return await ToDtosAsync(fields, cancellationToken);
    }

    public async Task<QualificationFieldDto> GetFieldAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var field = await FindFieldOrThrowAsync(id, cancellationToken);
        return (await ToDtosAsync(new[] { field }, cancellationToken))[0];
    }

    public async Task<QualificationFieldDto> CreateFieldAsync(
        CreateQualificationFieldRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var key = request.FieldKey.Trim().ToLowerInvariant();

        if (await _context.QualificationFields.AnyAsync(f => f.FieldKey == key, cancellationToken))
            throw new ConflictException($"A qualification field with key '{key}' already exists.");

        var count = await _context.QualificationFields.CountAsync(cancellationToken);
        if (count >= QualificationFieldLimits.MaxFieldsPerTenant)
        {
            throw Invalid("fieldKey",
                $"A tenant can have at most {QualificationFieldLimits.MaxFieldsPerTenant} qualification fields. " +
                "Deactivate one you no longer need first.");
        }

        var field = new QualificationField
        {
            FieldKey = key,
            DisplayName = request.DisplayName.Trim(),
            Description = Clean(request.Description),
            Question = request.Question.Trim(),
            DataType = Enum.Parse<QualificationDataType>(request.DataType, ignoreCase: true),
            IsRequired = request.IsRequired,
            Priority = request.Priority,
            ScoreWeight = request.ScoreWeight,
            AllowedValuesJson = SerializeAllowedValues(request.AllowedValues),
            ValidationPattern = Clean(request.ValidationPattern),
            IsActive = true,
            SortOrder = request.SortOrder,
            LastUpdatedBy = updatedByUserId
        };

        _context.QualificationFields.Add(field);
        await _context.SaveChangesAsync(cancellationToken);

        return await GetFieldAsync(field.Id, cancellationToken);
    }

    public async Task<QualificationFieldDto> UpdateFieldAsync(
        Guid id, UpdateQualificationFieldRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var field = await FindFieldOrThrowAsync(id, cancellationToken);
        var newType = Enum.Parse<QualificationDataType>(request.DataType, ignoreCase: true);

        // Changing the type of a field that already has values would leave those values normalized
        // under the old type's rules - "1 cr" normalized as Text is the literal string, as Currency it
        // is 10000000, and a scoring rule comparing them would silently stop matching. Re-normalizing
        // them here would be worse: it would rewrite what customers said based on a setting changed
        // afterwards. So the change is refused while values exist.
        if (newType != field.DataType)
        {
            var hasValues = await _context.LeadQualificationValues
                .AnyAsync(v => v.FieldId == id && !v.IsSuperseded, cancellationToken);

            if (hasValues)
            {
                throw Invalid("dataType",
                    "This field already has captured values, so its data type cannot be changed. " +
                    "Create a new field instead, and deactivate this one.");
            }
        }

        field.DisplayName = request.DisplayName.Trim();
        field.Description = Clean(request.Description);
        field.Question = request.Question.Trim();
        field.DataType = newType;
        field.IsRequired = request.IsRequired;
        field.Priority = request.Priority;
        field.ScoreWeight = request.ScoreWeight;
        field.AllowedValuesJson = SerializeAllowedValues(request.AllowedValues);
        field.ValidationPattern = Clean(request.ValidationPattern);
        field.SortOrder = request.SortOrder;
        field.LastUpdatedBy = updatedByUserId;

        await _context.SaveChangesAsync(cancellationToken);

        return await GetFieldAsync(id, cancellationToken);
    }

    public async Task<QualificationFieldDto> SetFieldActiveAsync(
        Guid id, bool isActive, Guid updatedByUserId, CancellationToken cancellationToken = default)
    {
        var field = await FindFieldOrThrowAsync(id, cancellationToken);

        if (field.IsActive != isActive)
        {
            field.IsActive = isActive;
            field.LastUpdatedBy = updatedByUserId;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return await GetFieldAsync(id, cancellationToken);
    }

    public async Task DeleteFieldAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var field = await FindFieldOrThrowAsync(id, cancellationToken);

        var capturedCount = await _context.LeadQualificationValues
            .CountAsync(v => v.FieldId == id, cancellationToken);

        if (capturedCount > 0)
        {
            throw new ConflictException(
                $"'{field.DisplayName}' has {capturedCount} captured value(s) and cannot be deleted. " +
                "Deactivate it instead - that removes it from the AI's questions while keeping what customers told you.");
        }

        field.IsDeleted = true;
        field.DeletedAt = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QualificationFieldDto>> ReorderFieldsAsync(
        ReorderQualificationFieldsRequest request, Guid updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _reorderValidator.ValidateAndThrowAsync(request, cancellationToken);

        var fields = await _context.QualificationFields.ToListAsync(cancellationToken);
        var byId = fields.ToDictionary(f => f.Id);

        var unknown = request.OrderedIds.Where(id => !byId.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            throw new NotFoundException($"Qualification field(s) not found: {string.Join(", ", unknown)}.");

        for (var i = 0; i < request.OrderedIds.Count; i++)
        {
            var field = byId[request.OrderedIds[i]];
            field.SortOrder = i;
            field.LastUpdatedBy = updatedByUserId;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return await GetFieldsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QualificationFieldDto>> SeedDefaultsAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters + explicit TenantId: this runs from signup and from the backfill job,
        // where the ambient tenant is not necessarily the one being seeded.
        var existingKeys = await _context.QualificationFields
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && !f.IsDeleted)
            .Select(f => f.FieldKey)
            .ToListAsync(cancellationToken);

        var existing = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);

        foreach (var seed in QualificationDefaults.Fields)
        {
            if (existing.Contains(seed.FieldKey))
                continue;

            _context.QualificationFields.Add(new QualificationField
            {
                TenantId = tenantId,
                FieldKey = seed.FieldKey,
                DisplayName = seed.DisplayName,
                Description = seed.Description,
                Question = seed.Question,
                DataType = seed.DataType,
                IsRequired = seed.IsRequired,
                Priority = seed.Priority,
                ScoreWeight = seed.ScoreWeight,
                IsActive = true,
                SortOrder = seed.SortOrder
            });
        }

        await _context.SaveChangesAsync(cancellationToken);

        var seeded = await _context.QualificationFields
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && !f.IsDeleted)
            .OrderByDescending(f => f.IsRequired)
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.SortOrder)
            .ToListAsync(cancellationToken);

        return seeded.Select(f => ToDto(f, 0)).ToList();
    }

    public async Task<LeadQualificationDto> GetLeadQualificationAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        await EnsureLeadExistsAsync(leadId, cancellationToken);

        var fields = await _context.QualificationFields
            .Where(f => f.IsActive)
            .ToListAsync(cancellationToken);

        var values = await _context.LeadQualificationValues
            .Where(v => v.LeadId == leadId && !v.IsSuperseded)
            .ToListAsync(cancellationToken);

        var byKey = fields.ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase);

        var captured = values
            // A value whose field was since deactivated or deleted is still shown - the customer did
            // tell us, and hiding it because the schema changed afterwards would lose real information.
            .Select(v => new LeadQualificationValueDto(
                v.Id,
                v.FieldKey,
                byKey.TryGetValue(v.FieldKey, out var f) ? f.DisplayName : v.FieldKey,
                v.RawValue,
                v.NormalizedValue,
                v.ExtractionConfidence,
                v.CapturedByUserId is not null,
                v.CapturedFromMessageId,
                v.CreatedAt))
            .OrderBy(v => v.DisplayName)
            .ToList();

        var capturedKeys = new HashSet<string>(values.Select(v => v.FieldKey), StringComparer.OrdinalIgnoreCase);

        var missingFields = fields
            .Where(f => !capturedKeys.Contains(f.FieldKey))
            .OrderByDescending(f => f.IsRequired)
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.SortOrder)
            .ThenBy(f => f.FieldKey)
            .ToList();

        return new LeadQualificationDto(
            leadId,
            captured,
            missingFields.Select(f => ToDto(f, 0)).ToList(),
            AllRequiredCaptured: fields.Where(f => f.IsRequired).All(f => capturedKeys.Contains(f.FieldKey)),
            CapturedCount: fields.Count(f => capturedKeys.Contains(f.FieldKey)),
            TotalCount: fields.Count);
    }

    public async Task<LeadQualificationDto> SetLeadValueAsync(
        Guid leadId, string fieldKey, SetLeadQualificationValueRequest request, Guid userId,
        CancellationToken cancellationToken = default)
    {
        await _setValueValidator.ValidateAndThrowAsync(request, cancellationToken);
        var lead = await FindLeadOrThrowAsync(leadId, cancellationToken);

        var key = fieldKey.Trim().ToLowerInvariant();
        var field = await _context.QualificationFields.FirstOrDefaultAsync(f => f.FieldKey == key, cancellationToken)
            ?? throw new NotFoundException("QualificationField", fieldKey);

        var normalized = QualificationValueNormalizer.Normalize(field, request.Value);
        if (!normalized.IsAccepted)
            throw Invalid("value", normalized.RejectionReason ?? "Value is not valid for this field.");

        await SupersedeExistingAsync(leadId, key, cancellationToken);

        _context.LeadQualificationValues.Add(new LeadQualificationValue
        {
            LeadId = leadId,
            FieldId = field.Id,
            FieldKey = key,
            RawValue = normalized.RawValue,
            NormalizedValue = normalized.NormalizedValue,
            CapturedByUserId = userId,
            // A human typed it. There is nothing to be unsure about, and anything below 1.0 would make
            // the agent ask again for something a colleague just entered.
            ExtractionConfidence = 1.0
        });

        MirrorOntoLead(lead, key, normalized.RawValue);
        lead.LastActivityAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return await GetLeadQualificationAsync(leadId, cancellationToken);
    }

    public async Task<LeadQualificationDto> ClearLeadValueAsync(
        Guid leadId, string fieldKey, CancellationToken cancellationToken = default)
    {
        var lead = await FindLeadOrThrowAsync(leadId, cancellationToken);
        var key = fieldKey.Trim().ToLowerInvariant();

        await SupersedeExistingAsync(leadId, key, cancellationToken);
        MirrorOntoLead(lead, key, null);

        await _context.SaveChangesAsync(cancellationToken);
        return await GetLeadQualificationAsync(leadId, cancellationToken);
    }

    /// <summary>Keeps Lead.Budget/Interest/PurchaseTimeline in step with the three mirrored field
    /// keys, so the existing lead list, filters and reports keep working while the UI moves to
    /// dynamic fields. A tenant whose schema has no such field simply never triggers this.</summary>
    private static void MirrorOntoLead(Lead lead, string fieldKey, string? rawValue)
    {
        switch (fieldKey)
        {
            case QualificationDefaults.BudgetKey:
                lead.Budget = rawValue;
                break;
            case QualificationDefaults.InterestKey:
                lead.Interest = rawValue;
                break;
            case QualificationDefaults.PurchaseTimelineKey:
                lead.PurchaseTimeline = rawValue;
                break;
        }
    }

    private async Task SupersedeExistingAsync(Guid leadId, string fieldKey, CancellationToken cancellationToken)
    {
        var current = await _context.LeadQualificationValues
            .Where(v => v.LeadId == leadId && v.FieldKey == fieldKey && !v.IsSuperseded)
            .ToListAsync(cancellationToken);

        foreach (var value in current)
            value.IsSuperseded = true;
    }

    private IQueryable<QualificationField> OrderedFields() =>
        _context.QualificationFields
            .OrderByDescending(f => f.IsRequired)
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.SortOrder)
            .ThenBy(f => f.FieldKey);

    /// <summary>One grouped count for the whole page rather than a count per field - the admin list
    /// shows this for every row, and a per-row query would be N+1 on a screen that always renders all
    /// of them.</summary>
    private async Task<IReadOnlyList<QualificationFieldDto>> ToDtosAsync(
        IReadOnlyCollection<QualificationField> fields, CancellationToken cancellationToken)
    {
        if (fields.Count == 0)
            return Array.Empty<QualificationFieldDto>();

        var ids = fields.Select(f => f.Id).ToList();

        var counts = await _context.LeadQualificationValues
            .Where(v => ids.Contains(v.FieldId) && !v.IsSuperseded)
            .GroupBy(v => v.FieldId)
            .Select(g => new { FieldId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FieldId, x => x.Count, cancellationToken);

        return fields
            .Select(f => ToDto(f, counts.TryGetValue(f.Id, out var c) ? c : 0))
            .ToList();
    }

    private static QualificationFieldDto ToDto(QualificationField f, int capturedLeadCount)
    {
        var allowed = QualificationValueNormalizer.ParseAllowedValues(f.AllowedValuesJson);

        return new QualificationFieldDto(
            f.Id,
            f.FieldKey,
            f.DisplayName,
            f.Description,
            f.Question,
            f.DataType.ToString(),
            f.IsRequired,
            f.Priority,
            f.ScoreWeight,
            allowed.Count > 0 ? allowed : null,
            f.ValidationPattern,
            f.IsActive,
            f.SortOrder,
            f.CreatedAt,
            f.UpdatedAt,
            capturedLeadCount);
    }

    private async Task<QualificationField> FindFieldOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.QualificationFields.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
        ?? throw new NotFoundException("QualificationField", id);

    private async Task<Lead> FindLeadOrThrowAsync(Guid leadId, CancellationToken cancellationToken) =>
        await _context.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken)
        ?? throw new NotFoundException("Lead", leadId);

    private async Task EnsureLeadExistsAsync(Guid leadId, CancellationToken cancellationToken)
    {
        if (!await _context.Leads.AnyAsync(l => l.Id == leadId, cancellationToken))
            throw new NotFoundException("Lead", leadId);
    }

    private static string? SerializeAllowedValues(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return null;

        var cleaned = values
            .Select(v => v?.Trim() ?? string.Empty)
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return cleaned.Count == 0 ? null : JsonSerializer.Serialize(cleaned);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
