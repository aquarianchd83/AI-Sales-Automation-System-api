using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>See <see cref="IQuestionnaireService"/>.</summary>
public class QuestionnaireService : IQuestionnaireService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly IDateTimeProvider _dateTime;

    public QuestionnaireService(IApplicationDbContext context, ITenantConfigOverrideProvider config, IDateTimeProvider dateTime)
    {
        _context = context;
        _config = config;
        _dateTime = dateTime;
    }

    public async Task<QuestionnaireStateDto> GetStateAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        var (response, latest) = await LoadAsync(tenantId, createIfMissing: true, cancellationToken);
        return await BuildStateAsync(tenantId, response, latest, cancellationToken);
    }

    public async Task<QuestionnaireStateDto> SaveAnswersAsync(Guid tenantId, SaveQuestionnaireAnswersRequest request, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        var (response, latest) = await LoadAsync(tenantId, createIfMissing: true, cancellationToken);
        if (response is null)
            throw new NotFoundException("There is no questionnaire for your business yet.");
        if (response.Status == QuestionnaireResponseStatus.Completed)
            throw new ConflictException("This questionnaire is already completed. Choose \"Update my answers\" to change them.");

        var definition = await DefinitionOfAsync(response.VersionId, cancellationToken);
        var merged = QuestionnaireEngine.ReadAnswers(response.AnswersJson);
        foreach (var (key, value) in request.Answers ?? new())
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                merged.Remove(key);
            else
                merged[key] = value.Clone();
        }

        var issues = QuestionnaireEngine.ValidateForSave(definition, merged);
        if (issues.Count > 0)
            throw ToValidationException(issues);

        response.AnswersJson = QuestionnaireEngine.WriteAnswers(QuestionnaireEngine.Prune(definition, merged));
        await _context.SaveChangesAsync(cancellationToken);
        return await BuildStateAsync(tenantId, response, latest, cancellationToken);
    }

    public async Task<QuestionnaireCompletionDto> CompleteAsync(Guid tenantId, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        var (response, latest) = await LoadAsync(tenantId, createIfMissing: false, cancellationToken);
        if (response is null)
            throw new NotFoundException("There is no questionnaire for your business yet.");
        if (response.Status == QuestionnaireResponseStatus.Completed)
            throw new ConflictException("This questionnaire is already completed.");

        var definition = await DefinitionOfAsync(response.VersionId, cancellationToken);
        var answers = QuestionnaireEngine.Prune(definition, QuestionnaireEngine.ReadAnswers(response.AnswersJson));

        var issues = QuestionnaireEngine.ValidateForComplete(definition, answers);
        if (issues.Count > 0)
            throw ToValidationException(issues);

        response.AnswersJson = QuestionnaireEngine.WriteAnswers(answers);
        response.Status = QuestionnaireResponseStatus.Completed;
        response.CompletedAtUtc = _dateTime.UtcNow;
        response.CompletedBy = actorUserId;

        var (created, updated, retired) = await SyncFactsAsync(tenantId, definition, answers, actorUserId, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new QuestionnaireCompletionDto(await BuildStateAsync(tenantId, response, latest, cancellationToken), created, updated, retired);
    }

    public async Task<QuestionnaireStateDto> RestartAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, cancellationToken);
        var (current, latest) = await LoadAsync(tenantId, createIfMissing: false, cancellationToken);
        if (current is null || latest is null)
            throw new NotFoundException("There is no questionnaire for your business yet.");
        if (current.Status == QuestionnaireResponseStatus.InProgress)
            return await BuildStateAsync(tenantId, current, latest, cancellationToken);

        var next = await StartAsync(tenantId, latest.Value.Template, latest.Value.Version, QuestionnaireEngine.ReadAnswers(current.AnswersJson), cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return await BuildStateAsync(tenantId, next, latest, cancellationToken);
    }

    // ----- loading ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The tenant's current response and the newest published version it could be on. An in-progress response on an older version of
    /// the same template moves up to the newer one, keeping every answer that still fits - the tenant has not finished, so nothing is lost.</summary>
    private async Task<(QuestionnaireResponse? Response, (QuestionnaireTemplate Template, QuestionnaireTemplateVersion Version)? Latest)> LoadAsync(
        Guid tenantId, bool createIfMissing, CancellationToken cancellationToken)
    {
        var latest = await ResolveLatestAsync(tenantId, cancellationToken);

        var response = await _context.QuestionnaireResponses.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (response is null)
        {
            if (!createIfMissing || latest is null)
                return (null, latest);

            response = await StartAsync(tenantId, latest.Value.Template, latest.Value.Version, new Dictionary<string, JsonElement>(), cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return (response, latest);
        }

        if (response.Status == QuestionnaireResponseStatus.InProgress && latest is { } l && l.Template.Id == response.TemplateId && l.Version.Id != response.VersionId)
        {
            var newer = QuestionnaireEngine.Parse(l.Version.DefinitionJson);
            response.VersionId = l.Version.Id;
            response.AnswersJson = QuestionnaireEngine.WriteAnswers(KeepWhatStillFits(newer, QuestionnaireEngine.ReadAnswers(response.AnswersJson)));
            await _context.SaveChangesAsync(cancellationToken);
        }

        return (response, latest);
    }

    /// <summary>The published questionnaire this tenant should be asked: the newest published version of the template for its industry,
    /// else of the generic one. A template with nothing published, or switched off, is skipped.</summary>
    private async Task<(QuestionnaireTemplate Template, QuestionnaireTemplateVersion Version)?> ResolveLatestAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var industry = await _context.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenantId).Select(t => t.Industry).FirstOrDefaultAsync(cancellationToken);
        var templates = await _context.QuestionnaireTemplates.Where(t => t.IsActive).ToListAsync(cancellationToken);

        var candidates = templates
            .Where(t => t.Industry != null && !string.IsNullOrWhiteSpace(industry) && string.Equals(t.Industry.Trim(), industry.Trim(), StringComparison.OrdinalIgnoreCase))
            .Concat(templates.Where(t => t.Industry == null))
            .ToList();

        foreach (var template in candidates)
        {
            var version = await _context.QuestionnaireTemplateVersions
                .Where(v => v.TemplateId == template.Id && v.Status == QuestionnaireVersionStatus.Published)
                .OrderByDescending(v => v.Number)
                .FirstOrDefaultAsync(cancellationToken);
            if (version is not null)
                return (template, version);
        }

        return null;
    }

    private async Task<QuestionnaireResponse> StartAsync(
        Guid tenantId, QuestionnaireTemplate template, QuestionnaireTemplateVersion version, IReadOnlyDictionary<string, JsonElement> carryOver, CancellationToken cancellationToken)
    {
        var definition = QuestionnaireEngine.Parse(version.DefinitionJson);
        var answers = KeepWhatStillFits(definition, carryOver);

        var tenant = await _context.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is not null)
        {
            foreach (var q in definition.Questions.Where(q => q.PrefillFrom is not null && !answers.ContainsKey(q.Key)))
            {
                var text = ProfileValue(tenant, q.PrefillFrom!)?.Trim();
                var max = q.MaxLength ?? (q.Type == QuestionType.ShortText ? QuestionnaireEngine.DefaultShortTextLength : QuestionnaireEngine.DefaultLongTextLength);
                if (!string.IsNullOrEmpty(text) && text.Length <= max)
                    answers[q.Key] = JsonSerializer.SerializeToElement(text);
            }
        }

        var response = new QuestionnaireResponse
        {
            TenantId = tenantId,
            TemplateId = template.Id,
            VersionId = version.Id,
            AnswersJson = QuestionnaireEngine.WriteAnswers(QuestionnaireEngine.Prune(definition, answers))
        };
        _context.QuestionnaireResponses.Add(response);
        return response;
    }

    /// <summary>The answers that are still valid for this definition: the question exists, the answer fits it, and it is asked.</summary>
    private static Dictionary<string, JsonElement> KeepWhatStillFits(QuestionnaireDefinition definition, IReadOnlyDictionary<string, JsonElement> answers)
    {
        var known = definition.Questions.Select(q => q.Key).ToHashSet(StringComparer.Ordinal);
        var candidate = answers.Where(a => known.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value.Clone(), StringComparer.Ordinal);

        var wrong = QuestionnaireEngine.ValidateForSave(definition, candidate).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in wrong)
            candidate.Remove(key);

        return QuestionnaireEngine.Prune(definition, candidate);
    }

    private static string? ProfileValue(Tenant tenant, string source) => source switch
    {
        "Name" => tenant.Name,
        "ProductName" => tenant.ProductName,
        "Industry" => tenant.Industry,
        "IndustrySubcategory" => tenant.IndustrySubcategory,
        "BusinessDescription" => tenant.BusinessDescription,
        "WebsiteUrl" => tenant.WebsiteUrl,
        "SupportEmail" => tenant.SupportEmail,
        "SupportPhone" => tenant.SupportPhone,
        "BusinessLocation" => tenant.BusinessLocation,
        "WorkingHours" => tenant.WorkingHours,
        "TargetAudience" => tenant.TargetAudience,
        "TargetLocation" => tenant.TargetLocation,
        _ => null
    };

    private async Task<QuestionnaireDefinition> DefinitionOfAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var json = await _context.QuestionnaireTemplateVersions.Where(v => v.Id == versionId).Select(v => v.DefinitionJson).FirstOrDefaultAsync(cancellationToken)
                   ?? throw new NotFoundException("The questionnaire version was not found.");
        return QuestionnaireEngine.Parse(json);
    }

    private async Task<QuestionnaireStateDto> BuildStateAsync(
        Guid tenantId, QuestionnaireResponse? response, (QuestionnaireTemplate Template, QuestionnaireTemplateVersion Version)? latest, CancellationToken cancellationToken)
    {
        if (response is null)
        {
            return new QuestionnaireStateDto(false, "There is no questionnaire for your business yet. Your platform administrator has not published one.",
                null, null, null, 0, QuestionnaireResponseStatus.InProgress, null, new Dictionary<string, JsonElement>(), Array.Empty<string>(), false, null);
        }

        var version = await _context.QuestionnaireTemplateVersions.FirstAsync(v => v.Id == response.VersionId, cancellationToken);
        var title = await _context.QuestionnaireTemplates.Where(t => t.Id == response.TemplateId).Select(t => t.Title).FirstAsync(cancellationToken);
        var definition = QuestionnaireEngine.Parse(version.DefinitionJson);
        var answers = QuestionnaireEngine.ReadAnswers(response.AnswersJson);
        var newer = latest is { } l && l.Template.Id == response.TemplateId && l.Version.Number > version.Number;

        return new QuestionnaireStateDto(
            true, null, response.Id, response.TemplateId, title, version.Number, response.Status, definition, answers,
            QuestionnaireEngine.VisibleKeys(definition, answers).ToList(), newer, response.CompletedAtUtc);
    }

    // ----- facts -------------------------------------------------------------------------------------------------------------------

    /// <summary>Makes the tenant's questionnaire facts match the answers: new ones are drafted, a changed one is updated (a verified one that
    /// changed goes back to Draft), and one whose question is no longer asked or answered is retired.</summary>
    private async Task<(int Created, int Updated, int Retired)> SyncFactsAsync(
        Guid tenantId, QuestionnaireDefinition definition, IReadOnlyDictionary<string, JsonElement> answers, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var wanted = QuestionnaireEngine.FactsFrom(definition, answers).ToDictionary(f => f.SourceRef, StringComparer.Ordinal);
        var existing = await _context.BusinessFacts.IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.SourceType == BusinessFactSource.Questionnaire && f.Status != BusinessFactStatus.Retired)
            .ToListAsync(cancellationToken);

        int created = 0, updated = 0, retired = 0;

        foreach (var fact in existing)
        {
            if (fact.SourceRef is null || !wanted.TryGetValue(fact.SourceRef, out var next))
            {
                fact.Status = BusinessFactStatus.Retired;
                fact.UpdatedBy = actorUserId;
                retired++;
                continue;
            }

            wanted.Remove(fact.SourceRef);
            if (fact.Statement == next.Statement && fact.Category == next.Category)
                continue;

            fact.Statement = next.Statement;
            fact.Category = next.Category;
            fact.UpdatedBy = actorUserId;
            if (fact.Status == BusinessFactStatus.Verified)
            {
                // The wording changed, so the person who confirmed the old words has not confirmed these.
                fact.Status = BusinessFactStatus.Draft;
                fact.VerifiedBy = null;
                fact.VerifiedAtUtc = null;
            }
            updated++;
        }

        foreach (var next in wanted.Values)
        {
            _context.BusinessFacts.Add(new BusinessFact
            {
                TenantId = tenantId,
                Category = next.Category,
                Statement = next.Statement,
                SourceType = BusinessFactSource.Questionnaire,
                SourceRef = next.SourceRef,
                Status = BusinessFactStatus.Draft,
                UpdatedBy = actorUserId
            });
            created++;
        }

        return (created, updated, retired);
    }

    private static ValidationException ToValidationException(IEnumerable<QuestionnaireIssue> issues) =>
        new(issues.Select(i => new ValidationFailure(i.Key, i.Message)));
}

/// <summary>The server-side half of the studio's per-tenant switch: a screen being hidden is never the only control.</summary>
public static class StudioGate
{
    public static async Task EnsureEnabledAsync(ITenantConfigOverrideProvider config, Guid tenantId, CancellationToken cancellationToken)
    {
        if (!(await config.GetStudioOptionsForTenantAsync(tenantId, cancellationToken)).Enabled)
            throw new FeatureDisabledException("The AI Content Studio is not switched on for your workspace.");
    }
}
