using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Setup;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Setup;

public class SetupAdminService : ISetupAdminService
{
    private readonly IApplicationDbContext _context;
    private readonly IValidator<SaveRequirementRequest> _requirementValidator;
    private readonly IValidator<SaveSetupVersionRequest> _versionValidator;
    private readonly IValidator<CreateSetupVersionRequest> _createValidator;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _clock;

    public SetupAdminService(
        IApplicationDbContext context,
        IValidator<SaveRequirementRequest> requirementValidator,
        IValidator<SaveSetupVersionRequest> versionValidator,
        IValidator<CreateSetupVersionRequest> createValidator,
        ICurrentUserService currentUser,
        IDateTimeProvider clock)
    {
        _context = context;
        _requirementValidator = requirementValidator;
        _versionValidator = versionValidator;
        _createValidator = createValidator;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<IReadOnlyList<PlanSetupSummaryDto>> GetPlansAsync(CancellationToken cancellationToken = default)
    {
        var plans = await _context.Plans.AsNoTracking().OrderBy(p => p.PriceMonthlyCents).ThenBy(p => p.Name).ToListAsync(cancellationToken);
        var versions = await _context.PlanSetupVersions.AsNoTracking().OrderByDescending(v => v.VersionNumber).ToListAsync(cancellationToken);
        var counts = await _context.PlanRequirements.AsNoTracking()
            .GroupBy(r => r.PlanSetupVersionId)
            .Select(g => new { VersionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VersionId, x => x.Count, cancellationToken);
        var apps = await ApplicationCountsAsync(cancellationToken);

        return plans.Select(plan => new PlanSetupSummaryDto(
                plan.Id, plan.Code, plan.Name, plan.IsActive,
                versions.Where(v => v.PlanId == plan.Id)
                    .Select(v => new SetupVersionSummaryDto(
                        v.Id, v.VersionNumber, v.Status, v.ReleaseNotes, v.ValidityDays,
                        counts.GetValueOrDefault(v.Id), apps.GetValueOrDefault(v.Id), v.PublishedAt, v.CreatedAt))
                    .ToList()))
            .ToList();
    }

    public async Task<SetupVersionDetailDto> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var (version, plan) = await LoadVersionAsync(versionId, cancellationToken);
        return await ToDetailAsync(version, plan, cancellationToken);
    }

    public async Task<SetupDefinitionDto> GetPreviewAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var (version, plan) = await LoadVersionAsync(versionId, cancellationToken);
        var requirements = await _context.PlanRequirements.AsNoTracking().Where(r => r.PlanSetupVersionId == versionId).ToListAsync(cancellationToken);
        return SetupMapper.BuildDefinition(plan, version, requirements);
    }

    public async Task<SetupVersionDetailDto> CreateVersionAsync(Guid planId, CreateSetupVersionRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), planId);

        var versions = await _context.PlanSetupVersions.Where(v => v.PlanId == planId).ToListAsync(cancellationToken);
        if (versions.Any(v => v.Status == SetupVersionStatus.Draft))
            throw new ConflictException("This plan already has a draft version - publish or delete it before starting another.");

        PlanSetupVersion? source = null;
        if (request.CloneFromVersionId is { } cloneId)
            source = versions.FirstOrDefault(v => v.Id == cloneId) ?? throw new NotFoundException(nameof(PlanSetupVersion), cloneId);
        else
            source = versions.Where(v => v.Status == SetupVersionStatus.Published).OrderByDescending(v => v.VersionNumber).FirstOrDefault()
                     ?? versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();

        var draft = new PlanSetupVersion
        {
            PlanId = planId,
            VersionNumber = (versions.Count == 0 ? 0 : versions.Max(v => v.VersionNumber)) + 1,
            Status = SetupVersionStatus.Draft,
            ReleaseNotes = request.ReleaseNotes?.Trim(),
            ValidityDays = source?.ValidityDays
        };
        _context.PlanSetupVersions.Add(draft);

        if (source is not null)
        {
            var sourceFields = await _context.PlanRequirements.AsNoTracking().Where(r => r.PlanSetupVersionId == source.Id).ToListAsync(cancellationToken);
            foreach (var field in sourceFields)
            {
                _context.PlanRequirements.Add(new PlanRequirement
                {
                    PlanSetupVersionId = draft.Id,
                    FieldKey = field.FieldKey,
                    Label = field.Label,
                    HelpText = field.HelpText,
                    FieldType = field.FieldType,
                    IsRequired = field.IsRequired,
                    DefaultValue = field.DefaultValue,
                    OptionsJson = field.OptionsJson,
                    ValidationJson = field.ValidationJson,
                    DisplayOrder = field.DisplayOrder,
                    Section = field.Section,
                    ConditionFieldKey = field.ConditionFieldKey,
                    ConditionOperator = field.ConditionOperator,
                    ConditionValue = field.ConditionValue,
                    MetricKey = field.MetricKey,
                    IsActive = field.IsActive
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(draft, plan, cancellationToken);
    }

    public async Task<SetupVersionDetailDto> UpdateVersionAsync(Guid versionId, SaveSetupVersionRequest request, CancellationToken cancellationToken = default)
    {
        await _versionValidator.ValidateAndThrowAsync(request, cancellationToken);
        var (version, plan) = await LoadVersionAsync(versionId, cancellationToken, track: true);
        EnsureDraft(version);

        version.ReleaseNotes = request.ReleaseNotes?.Trim();
        version.ValidityDays = request.ValidityDays;
        await _context.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(version, plan, cancellationToken);
    }

    public async Task<SetupVersionDetailDto> PublishVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var (version, plan) = await LoadVersionAsync(versionId, cancellationToken, track: true);
        EnsureDraft(version);

        var fields = await _context.PlanRequirements.AsNoTracking().Where(r => r.PlanSetupVersionId == versionId).ToListAsync(cancellationToken);
        var problems = CheckPublishable(fields);
        if (problems.Count > 0)
            throw new ValidationException(problems.Select(p => new ValidationFailure("version", p)));

        var previous = await _context.PlanSetupVersions
            .Where(v => v.PlanId == version.PlanId && v.Status == SetupVersionStatus.Published)
            .ToListAsync(cancellationToken);
        foreach (var old in previous)
            old.Status = SetupVersionStatus.Superseded;

        version.Status = SetupVersionStatus.Published;
        version.PublishedAt = _clock.UtcNow;
        version.PublishedBy = _currentUser.UserId;
        await _context.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(version, plan, cancellationToken);
    }

    public async Task DeleteVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var (version, _) = await LoadVersionAsync(versionId, cancellationToken, track: true);
        EnsureDraft(version);

        // Cascades to the version's requirements. A draft has never been handed to an application, so nothing else points at it.
        _context.PlanSetupVersions.Remove(version);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<SetupFieldDto> AddRequirementAsync(Guid versionId, SaveRequirementRequest request, CancellationToken cancellationToken = default)
    {
        await _requirementValidator.ValidateAndThrowAsync(request, cancellationToken);
        var (version, _) = await LoadVersionAsync(versionId, cancellationToken);
        EnsureDraft(version);

        var siblings = await _context.PlanRequirements.Where(r => r.PlanSetupVersionId == versionId).ToListAsync(cancellationToken);
        var key = request.FieldKey.Trim();
        if (siblings.Any(r => r.FieldKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException($"This version already has a field with the key '{key}'.");

        var requirement = new PlanRequirement { PlanSetupVersionId = versionId };
        Apply(requirement, request, key);
        EnsureCoherent(requirement, siblings, null);

        _context.PlanRequirements.Add(requirement);
        await _context.SaveChangesAsync(cancellationToken);
        return SetupMapper.ToDto(requirement);
    }

    public async Task<SetupFieldDto> UpdateRequirementAsync(Guid requirementId, SaveRequirementRequest request, CancellationToken cancellationToken = default)
    {
        await _requirementValidator.ValidateAndThrowAsync(request, cancellationToken);
        var requirement = await _context.PlanRequirements.FirstOrDefaultAsync(r => r.Id == requirementId, cancellationToken)
            ?? throw new NotFoundException(nameof(PlanRequirement), requirementId);
        var (version, _) = await LoadVersionAsync(requirement.PlanSetupVersionId, cancellationToken);
        EnsureDraft(version);

        var siblings = await _context.PlanRequirements.Where(r => r.PlanSetupVersionId == requirement.PlanSetupVersionId).ToListAsync(cancellationToken);
        var key = request.FieldKey.Trim();
        if (siblings.Any(r => r.Id != requirementId && r.FieldKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException($"This version already has a field with the key '{key}'.");

        // Renaming a key would orphan every other field's condition on it, and every answer stored under the old key.
        if (!key.Equals(requirement.FieldKey, StringComparison.Ordinal))
        {
            var dependants = siblings.Where(r => r.Id != requirementId && string.Equals(r.ConditionFieldKey, requirement.FieldKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (dependants.Count > 0)
                throw new ConflictException($"'{requirement.FieldKey}' is used by the condition of {string.Join(", ", dependants.Select(d => d.Label))} - change those first.");
        }

        Apply(requirement, request, key);
        EnsureCoherent(requirement, siblings, requirementId);

        await _context.SaveChangesAsync(cancellationToken);
        return SetupMapper.ToDto(requirement);
    }

    public async Task DeleteRequirementAsync(Guid requirementId, CancellationToken cancellationToken = default)
    {
        var requirement = await _context.PlanRequirements.FirstOrDefaultAsync(r => r.Id == requirementId, cancellationToken)
            ?? throw new NotFoundException(nameof(PlanRequirement), requirementId);
        var (version, _) = await LoadVersionAsync(requirement.PlanSetupVersionId, cancellationToken);
        EnsureDraft(version);

        var dependants = await _context.PlanRequirements
            .Where(r => r.PlanSetupVersionId == requirement.PlanSetupVersionId && r.Id != requirementId && r.ConditionFieldKey == requirement.FieldKey)
            .Select(r => r.Label)
            .ToListAsync(cancellationToken);
        if (dependants.Count > 0)
            throw new ConflictException($"'{requirement.Label}' is used by the condition of {string.Join(", ", dependants)} - change those first.");

        _context.PlanRequirements.Remove(requirement);
        await _context.SaveChangesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------------------

    private static void EnsureDraft(PlanSetupVersion version)
    {
        if (version.Status != SetupVersionStatus.Draft)
            throw new ConflictException(
                $"Version {version.VersionNumber} is {version.Status.ToString().ToLowerInvariant()} and can no longer be edited - " +
                "start a new version instead, so applications already running on it are not changed.");
    }

    private static void Apply(PlanRequirement requirement, SaveRequirementRequest request, string key)
    {
        requirement.FieldKey = key;
        requirement.Label = request.Label.Trim();
        requirement.HelpText = string.IsNullOrWhiteSpace(request.HelpText) ? null : request.HelpText.Trim();
        requirement.FieldType = request.FieldType;
        requirement.IsRequired = request.IsRequired;
        requirement.DefaultValue = string.IsNullOrWhiteSpace(request.DefaultValue) ? null : request.DefaultValue.Trim();
        requirement.OptionsJson = SaveRequirementRequestValidator.IsChoice(request.FieldType)
            ? SetupJson.WriteOptions(request.Options!.Select(o => new SetupOptionDto(o.Value.Trim(), o.Label.Trim())).ToList())
            : null;
        requirement.ValidationJson = SetupJson.WriteValidation(request.Validation);
        requirement.DisplayOrder = request.DisplayOrder;
        requirement.Section = request.Section.Trim().ToLowerInvariant();
        requirement.ConditionFieldKey = request.Condition?.FieldKey.Trim();
        requirement.ConditionOperator = request.Condition?.Operator;
        requirement.ConditionValue = request.Condition is { } c && c.Operator is not (SetupConditionOperator.Empty or SetupConditionOperator.NotEmpty)
            ? c.Value?.Trim()
            : null;
        requirement.MetricKey = string.IsNullOrWhiteSpace(request.MetricKey) ? null : request.MetricKey;
        requirement.IsActive = request.IsActive;
    }

    /// <summary>Rules that need the rest of the version: the condition's parent exists, the chain does not loop, and the
    /// default value is itself an acceptable answer.</summary>
    private static void EnsureCoherent(PlanRequirement requirement, IReadOnlyCollection<PlanRequirement> siblings, Guid? existingId)
    {
        var failures = new List<ValidationFailure>();
        var others = siblings.Where(r => r.Id != existingId).ToList();

        if (!string.IsNullOrWhiteSpace(requirement.ConditionFieldKey))
        {
            var parentKey = requirement.ConditionFieldKey;
            if (!others.Any(r => r.FieldKey.Equals(parentKey, StringComparison.OrdinalIgnoreCase)))
                failures.Add(new ValidationFailure("condition", $"The field '{parentKey}' this one depends on does not exist in this version."));
            else if (LoopsBack(requirement, others))
                failures.Add(new ValidationFailure("condition", "That dependency would loop back to this field."));
        }

        if (!string.IsNullOrWhiteSpace(requirement.DefaultValue) && requirement.FieldType != SetupFieldType.FileUpload)
        {
            var problem = SetupEvaluator.ValidateValue(requirement, DefaultAsStored(requirement));
            if (problem is not null)
                failures.Add(new ValidationFailure("defaultValue", $"The default value is not valid: {problem}"));
        }

        if (failures.Count > 0)
            throw new ValidationException(failures);
    }

    private static string? DefaultAsStored(PlanRequirement requirement) =>
        requirement.FieldType == SetupFieldType.MultiSelect && !(requirement.DefaultValue ?? string.Empty).TrimStart().StartsWith('[')
            ? SetupJson.WriteList((requirement.DefaultValue ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : requirement.DefaultValue;

    private static bool LoopsBack(PlanRequirement start, IReadOnlyCollection<PlanRequirement> others)
    {
        var current = start.ConditionFieldKey;
        var steps = 0;
        while (!string.IsNullOrWhiteSpace(current) && steps++ <= others.Count + 1)
        {
            if (current.Equals(start.FieldKey, StringComparison.OrdinalIgnoreCase))
                return true;
            current = others.FirstOrDefault(r => r.FieldKey.Equals(current, StringComparison.OrdinalIgnoreCase))?.ConditionFieldKey;
        }

        return false;
    }

    private static List<string> CheckPublishable(IReadOnlyCollection<PlanRequirement> fields)
    {
        var problems = new List<string>();
        var active = fields.Where(f => f.IsActive).ToList();
        if (active.Count == 0)
            problems.Add("A version needs at least one active field before it can be published.");

        foreach (var field in active.Where(f => !string.IsNullOrWhiteSpace(f.ConditionFieldKey)))
        {
            if (!active.Any(p => p.FieldKey.Equals(field.ConditionFieldKey, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"'{field.Label}' depends on '{field.ConditionFieldKey}', which is missing or inactive.");
        }

        foreach (var field in active.Where(f => SaveRequirementRequestValidator.IsChoice(f.FieldType)))
        {
            if (SetupJson.ParseOptions(field.OptionsJson).Count == 0)
                problems.Add($"'{field.Label}' is a choice field with no options.");
        }

        return problems;
    }

    private async Task<(PlanSetupVersion Version, Plan Plan)> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken, bool track = false)
    {
        var versions = track ? _context.PlanSetupVersions : _context.PlanSetupVersions.AsNoTracking();
        var version = await versions.FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            ?? throw new NotFoundException(nameof(PlanSetupVersion), versionId);
        var plan = await _context.Plans.AsNoTracking().FirstAsync(p => p.Id == version.PlanId, cancellationToken);
        return (version, plan);
    }

    private async Task<SetupVersionDetailDto> ToDetailAsync(PlanSetupVersion version, Plan plan, CancellationToken cancellationToken)
    {
        var fields = await _context.PlanRequirements.AsNoTracking()
            .Where(r => r.PlanSetupVersionId == version.Id)
            .OrderBy(r => r.DisplayOrder).ThenBy(r => r.Label)
            .ToListAsync(cancellationToken);
        var apps = await ApplicationCountsAsync(cancellationToken);

        return new SetupVersionDetailDto(
            version.Id, plan.Id, plan.Code, plan.Name, version.VersionNumber, version.Status, version.ReleaseNotes,
            version.ValidityDays, version.PublishedAt, apps.GetValueOrDefault(version.Id),
            fields.Select(SetupMapper.ToDto).ToList(),
            SetupSections.All.Select(s => new SetupSectionInfoDto(s.Key, s.Title, s.Description, s.Order)).ToList(),
            SetupMetrics.All);
    }

    /// <summary>Applications per version across ALL tenants. A platform-wide figure for the admin, so it deliberately
    /// steps outside the per-tenant query filter - same as the other platform-wide listings.</summary>
    private async Task<Dictionary<Guid, int>> ApplicationCountsAsync(CancellationToken cancellationToken) =>
        await _context.PlanApplications.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(a => a.PlanSetupVersionId)
            .Select(g => new { VersionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VersionId, x => x.Count, cancellationToken);
}
