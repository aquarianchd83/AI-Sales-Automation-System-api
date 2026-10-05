using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Setup;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Setup;

public class ApplicationSetupService : IApplicationSetupService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _clock;

    public ApplicationSetupService(IApplicationDbContext context, ITenantContext tenantContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    {
        _context = context;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _clock = clock;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AvailablePlanDto>> GetAvailablePlansAsync(CancellationToken cancellationToken = default)
    {
        var plans = await _context.Plans.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.PriceMonthlyCents).ToListAsync(cancellationToken);
        var published = await _context.PlanSetupVersions.AsNoTracking().Where(v => v.Status == SetupVersionStatus.Published).ToListAsync(cancellationToken);
        var ids = published.Select(v => v.Id).ToList();
        var fields = await _context.PlanRequirements.AsNoTracking().Where(r => ids.Contains(r.PlanSetupVersionId) && r.IsActive).ToListAsync(cancellationToken);

        var result = new List<AvailablePlanDto>();
        foreach (var plan in plans)
        {
            var version = published.FirstOrDefault(v => v.PlanId == plan.Id);
            if (version is null)
                continue;

            var own = fields.Where(f => f.PlanSetupVersionId == version.Id).ToList();
            var sections = own.Select(f => f.Section).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(SetupSections.Describe).OrderBy(s => s.Order).Select(s => s.Title).ToList();
            result.Add(new AvailablePlanDto(plan.Id, plan.Code, plan.Name, plan.PriceMonthlyCents, version.VersionNumber, own.Count, own.Count(f => f.IsRequired), sections));
        }

        return result;
    }

    public async Task<IReadOnlyList<ApplicationDto>> GetApplicationsAsync(CancellationToken cancellationToken = default)
    {
        var apps = await _context.PlanApplications.AsNoTracking()
            .Where(a => a.Status == ApplicationStatus.Active)
            .OrderByDescending(a => a.CreatedAt).Take(200)
            .ToListAsync(cancellationToken);
        if (apps.Count == 0)
            return Array.Empty<ApplicationDto>();

        var appIds = apps.Select(a => a.Id).ToList();
        var versionIds = apps.Select(a => a.PlanSetupVersionId).Distinct().ToList();
        var planIds = apps.Select(a => a.PlanId).Distinct().ToList();

        var plans = await _context.Plans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var versions = await _context.PlanSetupVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var requirements = (await _context.PlanRequirements.AsNoTracking().Where(r => versionIds.Contains(r.PlanSetupVersionId) && r.IsActive).ToListAsync(cancellationToken))
            .GroupBy(r => r.PlanSetupVersionId).ToDictionary(g => g.Key, g => g.ToList());
        var stored = (await _context.ApplicationSetupValues.AsNoTracking().Where(v => appIds.Contains(v.ApplicationId)).ToListAsync(cancellationToken))
            .GroupBy(v => v.ApplicationId).ToDictionary(g => g.Key, g => g.ToList());
        var latest = await LatestPublishedByPlanAsync(planIds, cancellationToken);

        return apps.Select(app =>
        {
            var reqs = requirements.GetValueOrDefault(app.PlanSetupVersionId) ?? new List<PlanRequirement>();
            var answers = EffectiveValues(reqs, stored.GetValueOrDefault(app.Id));
            var evaluation = SetupEvaluator.Evaluate(reqs, answers);
            return ToDto(app, plans[app.PlanId], versions[app.PlanSetupVersionId], latest, evaluation);
        }).ToList();
    }

    public async Task<ApplicationDto> GetApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(id, track: false, cancellationToken);
        return await ToDtoAsync(loaded, SetupEvaluator.Evaluate(loaded.Requirements, loaded.Effective()), cancellationToken);
    }

    public async Task<ApplicationSetupDto> GetSetupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(id, track: false, cancellationToken);
        return await ToSetupDtoAsync(loaded, cancellationToken);
    }

    public async Task<PagedResult<SetupAuditEntryDto>> GetAuditAsync(Guid id, PagedRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(id, track: false, cancellationToken);

        var query = _context.ApplicationSetupAuditEntries.AsNoTracking().Where(e => e.ApplicationId == id);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(e => e.PerformedAt).ThenByDescending(e => e.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync(cancellationToken);

        var userIds = rows.Where(r => r.PerformedBy.HasValue).Select(r => r.PerformedBy!.Value).Distinct().ToList();
        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName ?? u.Email ?? string.Empty, cancellationToken);

        // Labels come from the version each entry was made under, so history stays readable after fields are renamed.
        var versionIds = rows.Select(r => r.PlanSetupVersionId).Distinct().ToList();
        var labels = (await _context.PlanRequirements.AsNoTracking().Where(r => versionIds.Contains(r.PlanSetupVersionId)).ToListAsync(cancellationToken))
            .GroupBy(r => (r.PlanSetupVersionId, r.FieldKey)).ToDictionary(g => g.Key, g => g.First().Label);

        var items = rows.Select(r => new SetupAuditEntryDto(
            r.Id, r.Action, r.FieldKey,
            r.FieldKey is null ? null : labels.GetValueOrDefault((r.PlanSetupVersionId, r.FieldKey)) ?? r.FieldKey,
            r.PreviousValue, r.NewValue, r.Reason, r.PlanVersionLabel,
            r.PerformedBy, r.PerformedBy is { } who ? names.GetValueOrDefault(who) : null,
            r.ImpersonatedBy.HasValue, r.PerformedAt)).ToList();

        return new PagedResult<SetupAuditEntryDto>(items, total, request.Page, request.PageSize);
    }

    public async Task<IReadOnlyList<ApplicationExecutionDto>> GetExecutionsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await LoadAsync(id, track: false, cancellationToken);
        var rows = await _context.ApplicationExecutions.AsNoTracking().Where(e => e.ApplicationId == id)
            .OrderByDescending(e => e.StartedAt).Take(50).ToListAsync(cancellationToken);
        return rows.Select(ToExecutionDto).ToList();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------------------------------------------------

    public async Task<ApplicationDto> CreateAsync(CreateApplicationRequest request, CancellationToken cancellationToken = default)
    {
        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == request.PlanId && p.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), request.PlanId);
        var version = await PublishedVersionOfAsync(plan.Id, cancellationToken);

        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        if (name is { Length: > 150 })
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.Name), "Name must be at most 150 characters.") });

        var taken = await _context.PlanApplications.Select(a => a.Name).ToListAsync(cancellationToken);
        if (name is not null)
        {
            if (taken.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ConflictException($"You already have an application called '{name}'.");
        }
        else
        {
            name = plan.Name;
            for (var n = 2; taken.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
                name = $"{plan.Name} ({n})";
        }

        // Set explicitly (not left to the stamping interceptor) so the tenant audit log - which is built before stamping
        // runs - records this creation under the right tenant.
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Applications belong to a tenant.");
        var app = new PlanApplication
        {
            TenantId = tenantId,
            Name = name,
            PlanId = plan.Id,
            PlanSetupVersionId = version.Id,
            CreatedBy = _currentUser.UserId
        };
        _context.PlanApplications.Add(app);
        Audit(app, plan, version, SetupAuditAction.ApplicationCreated, newValue: SetupMapper.VersionLabel(plan, version));
        await _context.SaveChangesAsync(cancellationToken);

        var loaded = await LoadAsync(app.Id, track: false, cancellationToken);
        return await ToDtoAsync(loaded, SetupEvaluator.Evaluate(loaded.Requirements, loaded.Effective()), cancellationToken);
    }

    public async Task<SaveSetupResultDto> SaveSetupAsync(Guid id, SaveSetupRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(id, track: true, cancellationToken);
        EnsureActive(loaded.App);
        var now = _clock.UtcNow;

        var byKey = loaded.Requirements.ToDictionary(r => r.FieldKey, StringComparer.OrdinalIgnoreCase);
        var failures = new List<ValidationFailure>();
        var incoming = new List<(PlanRequirement Requirement, string? Value)>();

        foreach (var (key, element) in request.Values ?? new Dictionary<string, JsonElement>())
        {
            if (!byKey.TryGetValue(key, out var requirement))
            {
                failures.Add(new ValidationFailure(key, $"'{key}' is not a question of this application's plan."));
                continue;
            }

            var (value, error) = SetupValueCodec.Normalize(element, requirement.FieldType);
            if (error is not null)
                failures.Add(new ValidationFailure(key, error));
            else if (value is { Length: > SetupValueCodec.MaxStoredLength })
                failures.Add(new ValidationFailure(key, $"{requirement.Label} is too long."));
            else
                incoming.Add((requirement, value));
        }

        if (failures.Count > 0)
            throw new ValidationException(failures);

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > 500 })
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.Reason), "Reason must be at most 500 characters.") });

        var changed = 0;
        foreach (var (requirement, value) in incoming)
        {
            loaded.Stored.TryGetValue(requirement.FieldKey, out var row);
            var previous = row?.FieldValue;
            if (string.Equals(previous, value, StringComparison.Ordinal))
                continue;

            changed++;
            if (value is null)
            {
                if (row is not null)
                {
                    _context.ApplicationSetupValues.Remove(row);
                    loaded.Stored.Remove(requirement.FieldKey);
                }

                Audit(loaded.App, loaded.Plan, loaded.Version, SetupAuditAction.ValueCleared, requirement.FieldKey, previous, null, reason);
            }
            else
            {
                if (row is null)
                {
                    row = new ApplicationSetupValue { TenantId = loaded.App.TenantId, ApplicationId = loaded.App.Id, FieldKey = requirement.FieldKey };
                    _context.ApplicationSetupValues.Add(row);
                    loaded.Stored[requirement.FieldKey] = row;
                }

                row.FieldValue = value;
                row.UpdatedBy = _currentUser.UserId;
                Audit(loaded.App, loaded.Plan, loaded.Version, SetupAuditAction.ValueSet, requirement.FieldKey, previous, value, reason);
            }
        }

        var evaluation = SetupEvaluator.Evaluate(loaded.Requirements, loaded.Effective());
        var completing = request.Complete && evaluation.IsComplete;

        if (completing)
        {
            loaded.App.SetupCompletedAt = now;
            loaded.App.SetupCompletedBy = _currentUser.UserId;
            loaded.App.SetupExpiresAt = loaded.Version.ValidityDays is { } days ? now.AddDays(days) : null;
            Audit(loaded.App, loaded.Plan, loaded.Version, SetupAuditAction.SetupCompleted, newValue: "Completed", reason: reason);
        }

        loaded.App.SetupStatus = ComputeStatus(loaded.App, evaluation, loaded.Stored.Count > 0, completing, ApplicationSetupStatus.Incomplete, now);
        await _context.SaveChangesAsync(cancellationToken);

        var setup = await ToSetupDtoAsync(loaded, cancellationToken);
        return new SaveSetupResultDto(completing, changed, setup);
    }

    public async Task<ApplicationSetupDto> ChangePlanAsync(Guid id, ChangePlanRequest request, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(id, track: true, cancellationToken);
        EnsureActive(loaded.App);

        if (request.PlanId == loaded.App.PlanId)
            throw new ConflictException("This application is already on that plan. To pick up a newer version of it, migrate instead.");
        if (request.Reason is { Length: > 500 })
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.Reason), "Reason must be at most 500 characters.") });

        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == request.PlanId && p.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), request.PlanId);
        var version = await PublishedVersionOfAsync(plan.Id, cancellationToken);

        var from = SetupMapper.VersionLabel(loaded.Plan, loaded.Version);
        await MoveToVersionAsync(loaded, plan, version, SetupAuditAction.PlanChanged, from, request.Reason, cancellationToken);
        return await ToSetupDtoAsync(loaded, cancellationToken);
    }

    public async Task<ApplicationSetupDto> MigrateToLatestVersionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(id, track: true, cancellationToken);
        EnsureActive(loaded.App);

        var latest = await PublishedVersionOfAsync(loaded.Plan.Id, cancellationToken);
        if (latest.Id == loaded.Version.Id)
            throw new ConflictException("This application is already on the newest version of its plan.");

        var from = SetupMapper.VersionLabel(loaded.Plan, loaded.Version);
        await MoveToVersionAsync(loaded, loaded.Plan, latest, SetupAuditAction.VersionMigrated, from, null, cancellationToken);
        return await ToSetupDtoAsync(loaded, cancellationToken);
    }

    public async Task<ApplicationExecutionDto> ExecuteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(id, track: true, cancellationToken);
        EnsureActive(loaded.App);
        var now = _clock.UtcNow;

        // Never trust the stored status alone: revalidate against the requirements and answers as they are right now.
        var answers = loaded.Effective();
        var evaluation = SetupEvaluator.Evaluate(loaded.Requirements, answers);
        var status = EffectiveStatus(loaded.App, now);

        if (status != ApplicationSetupStatus.Completed || !evaluation.IsComplete)
        {
            if (loaded.App.SetupStatus == ApplicationSetupStatus.Completed && !evaluation.IsComplete)
                loaded.App.SetupStatus = ApplicationSetupStatus.RequiresUpdate;

            var labels = loaded.Requirements.ToDictionary(r => r.FieldKey, r => r.Label, StringComparer.OrdinalIgnoreCase);
            var missing = evaluation.Missing.Concat(evaluation.Invalid).Select(i => labels.GetValueOrDefault(i.FieldKey) ?? i.FieldKey).ToList();

            Audit(loaded.App, loaded.Plan, loaded.Version, SetupAuditAction.ExecutionBlocked, newValue: status.ToString());
            await _context.SaveChangesAsync(cancellationToken);
            throw new SetupIncompleteException(missing);
        }

        // Frozen copy: later edits to the live setup apply from the NEXT execution, never to this one.
        var snapshot = evaluation.VisibleKeys.ToDictionary(
            key => key, key => answers.TryGetValue(key, out var v) ? v : null, StringComparer.OrdinalIgnoreCase);

        var execution = new ApplicationExecution
        {
            TenantId = loaded.App.TenantId,
            ApplicationId = loaded.App.Id,
            PlanId = loaded.Plan.Id,
            PlanSetupVersionId = loaded.Version.Id,
            PlanVersionLabel = SetupMapper.VersionLabel(loaded.Plan, loaded.Version),
            SetupSnapshotJson = JsonSerializer.Serialize(snapshot),
            StartedBy = _currentUser.UserId,
            StartedAt = now
        };
        _context.ApplicationExecutions.Add(execution);

        loaded.App.LastExecutedAt = now;
        loaded.App.ExecutionCount++;
        Audit(loaded.App, loaded.Plan, loaded.Version, SetupAuditAction.Executed, newValue: execution.PlanVersionLabel);
        await _context.SaveChangesAsync(cancellationToken);

        return ToExecutionDto(execution);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Re-points the application at another plan/version. Answers are never touched: those the new requirements ask
    /// for carry over (by field key), the rest stay stored but ignored, so moving back restores them. The setup is
    /// then recalculated - and an application that WAS complete but now lacks answers becomes RequiresUpdate.
    /// </summary>
    private async Task MoveToVersionAsync(
        Loaded loaded, Plan plan, PlanSetupVersion version, SetupAuditAction action, string from, string? reason, CancellationToken cancellationToken)
    {
        var requirements = await _context.PlanRequirements.AsNoTracking()
            .Where(r => r.PlanSetupVersionId == version.Id && r.IsActive).ToListAsync(cancellationToken);

        loaded.App.PlanId = plan.Id;
        loaded.App.PlanSetupVersionId = version.Id;
        loaded.Plan = plan;
        loaded.Version = version;
        loaded.Requirements = requirements;

        var evaluation = SetupEvaluator.Evaluate(requirements, loaded.Effective());
        var now = _clock.UtcNow;
        loaded.App.SetupStatus = ComputeStatus(loaded.App, evaluation, loaded.Stored.Count > 0, false, ApplicationSetupStatus.RequiresUpdate, now);

        // The validity window belongs to the version, so a still-complete setup takes on the new version's.
        if (loaded.App.SetupCompletedAt is { } completedAt)
            loaded.App.SetupExpiresAt = version.ValidityDays is { } days ? completedAt.AddDays(days) : null;

        Audit(loaded.App, plan, version, action, previous: from, newValue: SetupMapper.VersionLabel(plan, version), reason: reason);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// <list type="bullet">
    /// <item>Just confirmed -> Completed.</item>
    /// <item>Confirmed at some point before: still valid -> Completed; no longer valid -> <paramref name="whenInvalid"/>
    /// (Incomplete after an edit, RequiresUpdate after a plan change or migration).</item>
    /// <item>Never confirmed: InProgress once any answer exists, else NotStarted.</item>
    /// </list>
    /// A Completed setup past its expiry is Expired until it is confirmed again.
    /// </summary>
    internal static ApplicationSetupStatus ComputeStatus(
        PlanApplication app, SetupEvaluation evaluation, bool hasAnswers, bool completing, ApplicationSetupStatus whenInvalid, DateTime now)
    {
        ApplicationSetupStatus status;
        if (completing)
            status = ApplicationSetupStatus.Completed;
        else if (app.SetupCompletedAt is not null)
            status = evaluation.IsComplete ? ApplicationSetupStatus.Completed : whenInvalid;
        else
            status = hasAnswers ? ApplicationSetupStatus.InProgress : ApplicationSetupStatus.NotStarted;

        return status == ApplicationSetupStatus.Completed && app.SetupExpiresAt is { } expires && expires <= now
            ? ApplicationSetupStatus.Expired
            : status;
    }

    private static ApplicationSetupStatus EffectiveStatus(PlanApplication app, DateTime now) =>
        app.SetupStatus == ApplicationSetupStatus.Completed && app.SetupExpiresAt is { } expires && expires <= now
            ? ApplicationSetupStatus.Expired
            : app.SetupStatus;

    private static void EnsureActive(PlanApplication app)
    {
        if (app.Status != ApplicationStatus.Active)
            throw new ConflictException("This application is archived.");
    }

    /// <summary>Stored answers with each requirement's default filled in where no answer was given.</summary>
    internal static Dictionary<string, string?> EffectiveValues(IEnumerable<PlanRequirement> requirements, IEnumerable<ApplicationSetupValue>? stored)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in stored ?? Enumerable.Empty<ApplicationSetupValue>())
            values[row.FieldKey] = row.FieldValue;

        foreach (var requirement in requirements)
        {
            if (string.IsNullOrWhiteSpace(requirement.DefaultValue))
                continue;
            if (!values.TryGetValue(requirement.FieldKey, out var existing) || string.IsNullOrWhiteSpace(existing))
            {
                values[requirement.FieldKey] = requirement.FieldType == SetupFieldType.MultiSelect && !requirement.DefaultValue.TrimStart().StartsWith('[')
                    ? SetupJson.WriteList(requirement.DefaultValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    : requirement.DefaultValue;
            }
        }

        return values;
    }

    private void Audit(
        PlanApplication app, Plan plan, PlanSetupVersion version, SetupAuditAction action,
        string? fieldKey = null, string? previous = null, string? newValue = null, string? reason = null)
    {
        _context.ApplicationSetupAuditEntries.Add(new ApplicationSetupAuditEntry
        {
            TenantId = app.TenantId,
            ApplicationId = app.Id,
            PlanSetupVersionId = version.Id,
            PlanVersionLabel = SetupMapper.VersionLabel(plan, version),
            Action = action,
            FieldKey = fieldKey,
            PreviousValue = previous,
            NewValue = newValue,
            Reason = reason,
            PerformedBy = _currentUser.UserId,
            ImpersonatedBy = _currentUser.ImpersonatorUserId,
            PerformedAt = _clock.UtcNow
        });
    }

    private async Task<PlanSetupVersion> PublishedVersionOfAsync(Guid planId, CancellationToken cancellationToken) =>
        await _context.PlanSetupVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PlanId == planId && v.Status == SetupVersionStatus.Published, cancellationToken)
        ?? throw new ConflictException("This plan has no published setup yet, so an application cannot be started on it.");

    private async Task<Dictionary<Guid, PlanSetupVersion>> LatestPublishedByPlanAsync(IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken) =>
        await _context.PlanSetupVersions.AsNoTracking()
            .Where(v => planIds.Contains(v.PlanId) && v.Status == SetupVersionStatus.Published)
            .ToDictionaryAsync(v => v.PlanId, cancellationToken);

    private sealed class Loaded
    {
        public required PlanApplication App { get; init; }

        public required Plan Plan { get; set; }

        public required PlanSetupVersion Version { get; set; }

        public required List<PlanRequirement> Requirements { get; set; }

        public required Dictionary<string, ApplicationSetupValue> Stored { get; init; }

        public Dictionary<string, string?> Effective() => EffectiveValues(Requirements, Stored.Values);
    }

    private async Task<Loaded> LoadAsync(Guid id, bool track, CancellationToken cancellationToken)
    {
        var apps = track ? _context.PlanApplications : _context.PlanApplications.AsNoTracking();
        var app = await apps.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(PlanApplication), id);

        var plan = await _context.Plans.AsNoTracking().FirstAsync(p => p.Id == app.PlanId, cancellationToken);
        var version = await _context.PlanSetupVersions.AsNoTracking().FirstAsync(v => v.Id == app.PlanSetupVersionId, cancellationToken);
        var requirements = await _context.PlanRequirements.AsNoTracking()
            .Where(r => r.PlanSetupVersionId == version.Id && r.IsActive).ToListAsync(cancellationToken);
        var values = track ? _context.ApplicationSetupValues : _context.ApplicationSetupValues.AsNoTracking();
        var stored = await values.Where(v => v.ApplicationId == id).ToDictionaryAsync(v => v.FieldKey, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return new Loaded { App = app, Plan = plan, Version = version, Requirements = requirements, Stored = stored };
    }

    private async Task<ApplicationDto> ToDtoAsync(Loaded loaded, SetupEvaluation evaluation, CancellationToken cancellationToken)
    {
        var latest = await LatestPublishedByPlanAsync(new[] { loaded.Plan.Id }, cancellationToken);
        return ToDto(loaded.App, loaded.Plan, loaded.Version, latest, evaluation);
    }

    private ApplicationDto ToDto(
        PlanApplication app, Plan plan, PlanSetupVersion version, IReadOnlyDictionary<Guid, PlanSetupVersion> latestByPlan, SetupEvaluation evaluation)
    {
        var status = EffectiveStatus(app, _clock.UtcNow);
        var newer = latestByPlan.TryGetValue(plan.Id, out var latest) && latest.Id != version.Id && latest.VersionNumber > version.VersionNumber;
        return new ApplicationDto(
            app.Id, app.Name, plan.Id, plan.Code, plan.Name, version.Id, version.VersionNumber, newer, app.Status, status,
            evaluation.PercentComplete, app.SetupCompletedAt, app.SetupExpiresAt, app.LastExecutedAt, app.ExecutionCount,
            app.Status == ApplicationStatus.Active && status == ApplicationSetupStatus.Completed && evaluation.IsComplete,
            app.CreatedAt);
    }

    private async Task<ApplicationSetupDto> ToSetupDtoAsync(Loaded loaded, CancellationToken cancellationToken)
    {
        var effective = loaded.Effective();
        var evaluation = SetupEvaluator.Evaluate(loaded.Requirements, effective);
        var typed = loaded.Requirements.ToDictionary(
            r => r.FieldKey,
            r => SetupValueCodec.ToTyped(effective.GetValueOrDefault(r.FieldKey), r.FieldType),
            StringComparer.OrdinalIgnoreCase);

        var visible = loaded.Requirements.Where(r => evaluation.VisibleKeys.Contains(r.FieldKey, StringComparer.OrdinalIgnoreCase));
        var projection = SetupProjectionCalculator.Calculate(visible, effective);

        return new ApplicationSetupDto(
            await ToDtoAsync(loaded, evaluation, cancellationToken),
            SetupMapper.BuildDefinition(loaded.Plan, loaded.Version, loaded.Requirements),
            typed, evaluation.ToDto(), projection);
    }

    private static ApplicationExecutionDto ToExecutionDto(ApplicationExecution e) =>
        new(e.Id, e.ApplicationId, e.PlanVersionLabel, e.StartedAt, JsonSerializer.Deserialize<Dictionary<string, string?>>(e.SetupSnapshotJson)?.Count ?? 0);
}
