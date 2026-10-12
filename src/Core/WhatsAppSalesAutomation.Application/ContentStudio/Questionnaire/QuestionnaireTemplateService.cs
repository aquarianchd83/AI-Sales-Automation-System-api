using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>See <see cref="IQuestionnaireTemplateService"/>. Everything that changes what tenants are asked is written to the platform audit trail.</summary>
public class QuestionnaireTemplateService : IQuestionnaireTemplateService
{
    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9-]{1,59}$", RegexOptions.Compiled);

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IPlatformAuditService _audit;

    public QuestionnaireTemplateService(IApplicationDbContext context, IDateTimeProvider dateTime, IPlatformAuditService audit)
    {
        _context = context;
        _dateTime = dateTime;
        _audit = audit;
    }

    public async Task<IReadOnlyList<QuestionnaireTemplateSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var templates = await _context.QuestionnaireTemplates.OrderBy(t => t.Title).ToListAsync(cancellationToken);
        var versions = await _context.QuestionnaireTemplateVersions.Select(v => new { v.TemplateId, v.Number, v.Status }).ToListAsync(cancellationToken);
        return templates.Select(t => Summary(t, versions.Where(v => v.TemplateId == t.Id).Select(v => (v.Number, v.Status)).ToList())).ToList();
    }

    public async Task<QuestionnaireTemplateDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var template = await FindAsync(id, cancellationToken);
        return await DetailAsync(template, cancellationToken);
    }

    public async Task<QuestionnaireTemplateDetailDto> CreateAsync(CreateQuestionnaireTemplateRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var failures = new List<ValidationFailure>();
        if (!KeyPattern.IsMatch(request.Key ?? string.Empty))
            failures.Add(new ValidationFailure(nameof(request.Key), "The key must be 2-60 characters: lower-case letters, digits and hyphens, starting with a letter."));
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 150)
            failures.Add(new ValidationFailure(nameof(request.Title), "Give the template a title of up to 150 characters."));
        if (request.Industry?.Trim().Length > 100)
            failures.Add(new ValidationFailure(nameof(request.Industry), "The industry is longer than 100 characters."));
        if (failures.Count > 0)
            throw new ValidationException(failures);

        var (_, issues) = QuestionnaireEngine.TryParse(request.DefinitionJson ?? string.Empty);
        if (issues.Count > 0)
            throw ToValidationException(issues);

        var key = request.Key!.Trim();
        if (await _context.QuestionnaireTemplates.AnyAsync(t => t.Key == key, cancellationToken))
            throw new ConflictException($"A template with the key '{key}' already exists.");

        var template = new QuestionnaireTemplate
        {
            Key = key,
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Industry = string.IsNullOrWhiteSpace(request.Industry) ? null : request.Industry.Trim()
        };
        _context.QuestionnaireTemplates.Add(template);
        _context.QuestionnaireTemplateVersions.Add(new QuestionnaireTemplateVersion { TemplateId = template.Id, Number = 1, DefinitionJson = request.DefinitionJson! });
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, "StudioQuestionnaireCreated", details: $"{template.Key}", cancellationToken: cancellationToken);
        return await DetailAsync(template, cancellationToken);
    }

    public async Task<QuestionnaireTemplateDetailDto> UpdateAsync(Guid id, UpdateQuestionnaireTemplateRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 150)
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.Title), "Give the template a title of up to 150 characters.") });

        var template = await FindAsync(id, cancellationToken);
        template.Title = request.Title.Trim();
        template.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        template.Industry = string.IsNullOrWhiteSpace(request.Industry) ? null : request.Industry.Trim();
        template.IsActive = request.IsActive;
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, "StudioQuestionnaireUpdated", details: $"{template.Key} (active: {template.IsActive})", cancellationToken: cancellationToken);
        return await DetailAsync(template, cancellationToken);
    }

    public async Task<QuestionnaireTemplateDetailDto> CreateVersionAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var template = await FindAsync(id, cancellationToken);
        var versions = await _context.QuestionnaireTemplateVersions.Where(v => v.TemplateId == id).OrderBy(v => v.Number).ToListAsync(cancellationToken);

        if (versions.Any(v => v.Status == QuestionnaireVersionStatus.Draft))
            throw new ConflictException("There is already a draft version. Publish it or edit it first.");

        var latest = versions.Last();
        _context.QuestionnaireTemplateVersions.Add(new QuestionnaireTemplateVersion { TemplateId = id, Number = latest.Number + 1, DefinitionJson = latest.DefinitionJson });
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, "StudioQuestionnaireVersionCreated", details: $"{template.Key} v{latest.Number + 1}", cancellationToken: cancellationToken);
        return await DetailAsync(template, cancellationToken);
    }

    public async Task<QuestionnaireTemplateDetailDto> SaveVersionAsync(Guid id, int number, SaveQuestionnaireVersionRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var template = await FindAsync(id, cancellationToken);
        var version = await FindVersionAsync(id, number, cancellationToken);
        if (version.Status == QuestionnaireVersionStatus.Published)
            throw new ConflictException("A published version cannot be changed. Create a new version to make changes.");

        // A draft may be saved while unfinished, but it must at least be readable.
        if (QuestionnaireEngine.ReadProblem(request.DefinitionJson ?? string.Empty) is { } problem)
            throw ToValidationException(new[] { new QuestionnaireIssue("definition", problem) });

        version.DefinitionJson = request.DefinitionJson!;
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, "StudioQuestionnaireDraftSaved", details: $"{template.Key} v{number}", cancellationToken: cancellationToken);
        return await DetailAsync(template, cancellationToken);
    }

    public async Task<QuestionnaireTemplateDetailDto> PublishVersionAsync(Guid id, int number, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var template = await FindAsync(id, cancellationToken);
        var version = await FindVersionAsync(id, number, cancellationToken);
        if (version.Status == QuestionnaireVersionStatus.Published)
            throw new ConflictException("This version is already published.");

        var (_, issues) = QuestionnaireEngine.TryParse(version.DefinitionJson);
        if (issues.Count > 0)
            throw ToValidationException(issues);

        version.Status = QuestionnaireVersionStatus.Published;
        version.PublishedAtUtc = _dateTime.UtcNow;
        version.PublishedBy = actorUserId;
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(actorUserId, actorEmail, "StudioQuestionnairePublished", details: $"{template.Key} v{number}", cancellationToken: cancellationToken);
        return await DetailAsync(template, cancellationToken);
    }

    public QuestionnaireValidationDto Validate(string definitionJson)
    {
        var (definition, issues) = QuestionnaireEngine.TryParse(definitionJson ?? string.Empty);
        return new QuestionnaireValidationDto(issues.Count == 0, issues, definition?.Questions.Count ?? 0);
    }

    public string GetStarterDefinition() => QuestionnaireEngine.StarterDefinitionJson();

    private async Task<QuestionnaireTemplate> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.QuestionnaireTemplates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
        ?? throw new NotFoundException("Questionnaire template not found.");

    private async Task<QuestionnaireTemplateVersion> FindVersionAsync(Guid id, int number, CancellationToken cancellationToken) =>
        await _context.QuestionnaireTemplateVersions.FirstOrDefaultAsync(v => v.TemplateId == id && v.Number == number, cancellationToken)
        ?? throw new NotFoundException("Version not found.");

    private async Task<QuestionnaireTemplateDetailDto> DetailAsync(QuestionnaireTemplate template, CancellationToken cancellationToken)
    {
        var versions = await _context.QuestionnaireTemplateVersions.Where(v => v.TemplateId == template.Id).OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        return new QuestionnaireTemplateDetailDto(
            Summary(template, versions.Select(v => (v.Number, v.Status)).ToList()),
            versions.Select(v => new QuestionnaireVersionDto(v.Id, v.Number, v.Status, v.DefinitionJson, CountQuestions(v.DefinitionJson), v.PublishedAtUtc)).ToList());
    }

    private static QuestionnaireTemplateSummaryDto Summary(QuestionnaireTemplate t, IReadOnlyList<(int Number, QuestionnaireVersionStatus Status)> versions) => new(
        t.Id, t.Key, t.Title, t.Description, t.Industry, t.IsActive,
        versions.Count == 0 ? 0 : versions.Max(v => v.Number),
        versions.Where(v => v.Status == QuestionnaireVersionStatus.Published).Select(v => (int?)v.Number).Max(),
        t.UpdatedAt ?? t.CreatedAt);

    private static int CountQuestions(string json) => QuestionnaireEngine.TryParse(json).Definition?.Questions.Count ?? 0;

    private static ValidationException ToValidationException(IEnumerable<QuestionnaireIssue> issues) =>
        new(issues.Select(i => new ValidationFailure(i.Key, i.Message)));
}
