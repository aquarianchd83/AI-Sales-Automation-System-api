using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.MessageTemplates;

public class MessageTemplateService : IMessageTemplateService
{
    private readonly IApplicationDbContext _context;
    private readonly IWhatsAppService _whatsApp;
    private readonly IValidator<CreateMessageTemplateRequest> _createValidator;
    private readonly IValidator<UpdateMessageTemplateRequest> _updateValidator;
    private readonly IValidator<ReviewMessageTemplateRequest> _reviewValidator;

    public MessageTemplateService(
        IApplicationDbContext context,
        IWhatsAppService whatsApp,
        IValidator<CreateMessageTemplateRequest> createValidator,
        IValidator<UpdateMessageTemplateRequest> updateValidator,
        IValidator<ReviewMessageTemplateRequest> reviewValidator)
    {
        _context = context;
        _whatsApp = whatsApp;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _reviewValidator = reviewValidator;
    }

    public async Task<PagedResult<MessageTemplateDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.MessageTemplates.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(t => t.Name.Contains(search) || t.WhatsAppTemplateName.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<MessageTemplateDto>(items.Select(ToDto).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<MessageTemplateDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await FindOrThrowAsync(id, cancellationToken));

    public async Task<MessageTemplateDto> CreateAsync(CreateMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var exists = await _context.MessageTemplates.AnyAsync(
            t => t.WhatsAppTemplateName == request.WhatsAppTemplateName && t.Language == request.Language,
            cancellationToken);
        if (exists)
            throw new ConflictException($"A template named '{request.WhatsAppTemplateName}' already exists for language '{request.Language}'.");

        var template = new MessageTemplate
        {
            Name = request.Name,
            Language = request.Language,
            Category = Enum.Parse<TemplateCategory>(request.Category, ignoreCase: true),
            WhatsAppTemplateName = request.WhatsAppTemplateName,
            BodyText = request.BodyText,
            WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending
        };

        _context.MessageTemplates.Add(template);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(template);
    }

    public async Task<MessageTemplateDto> UpdateAsync(Guid id, UpdateMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var template = await FindOrThrowAsync(id, cancellationToken);

        if (request.WhatsAppTemplateName is not null && request.WhatsAppTemplateName != template.WhatsAppTemplateName)
        {
            // Meta does not allow renaming a template after creation (name/language/category are
            // fixed at creation time) - once MetaTemplateId is set, this system mirrors that
            // constraint rather than silently drifting the local name out of sync with what Meta
            // actually calls it, which SendTemplateMessageAsync relies on matching exactly.
            if (template.MetaTemplateId is not null)
                throw new ConflictException(
                    $"'{template.WhatsAppTemplateName}' has already been created on Meta and its name cannot be changed there - create a new template instead of renaming this one.");

            var nameInUse = await _context.MessageTemplates.AnyAsync(
                t => t.Id != id && t.WhatsAppTemplateName == request.WhatsAppTemplateName && t.Language == template.Language,
                cancellationToken);
            if (nameInUse)
                throw new ConflictException($"A template named '{request.WhatsAppTemplateName}' already exists for language '{template.Language}'.");

            template.WhatsAppTemplateName = request.WhatsAppTemplateName;
        }

        // Editing the wording of an already-approved template is exactly what Meta requires a fresh
        // review for - a change to Approved content silently staying Approved would let an
        // unreviewed message go out under an approved template's name.
        if (template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved && request.BodyText != template.BodyText)
            template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;

        template.BodyText = request.BodyText;
        template.IsActive = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(template);
    }

    public async Task<MessageTemplateDto> ReviewAsync(Guid id, ReviewMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _reviewValidator.ValidateAndThrowAsync(request, cancellationToken);

        var template = await FindOrThrowAsync(id, cancellationToken);
        template.WhatsAppTemplateStatus = Enum.Parse<WhatsAppTemplateStatus>(request.Status, ignoreCase: true);

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(template);
    }

    public async Task<TemplateSyncResultDto> SyncWithMetaAsync(CancellationToken cancellationToken = default)
    {
        var (pushCandidateCount, createdCount, updatedContentCount, pushFailures) = await PushToMetaAsync(null, cancellationToken);
        var (remoteCount, matchedCount, statusUpdatedCount, unmatched) = await PullFromMetaAsync(null, cancellationToken);

        return new TemplateSyncResultDto(
            pushCandidateCount, createdCount, updatedContentCount, pushFailures,
            remoteCount, matchedCount, statusUpdatedCount, unmatched);
    }

    /// <summary>Same push-then-pull cycle as SyncWithMetaAsync, scoped to a single template - the
    /// per-row "Sync" button. Runs the push phase even if the template is inactive (an explicit,
    /// user-initiated sync on one row is a deliberate action, unlike the bulk job's active-only
    /// filter which exists to avoid wasting Meta's limited template slots on abandoned drafts) then
    /// the pull phase to immediately reflect Meta's resulting status.</summary>
    public async Task<MessageTemplateSyncOneResultDto> SyncOneAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var template = await FindOrThrowAsync(id, cancellationToken);

        var (_, _, _, pushFailures) = await PushToMetaAsync(id, cancellationToken);
        await PullFromMetaAsync(id, cancellationToken);

        // Re-fetch: both phases may have mutated this row (tracked by the same DbContext, so no
        // extra query is strictly needed, but re-reading keeps this resilient to either phase
        // detaching/reloading the entity in the future).
        var refreshed = await FindOrThrowAsync(id, cancellationToken);
        var pushError = pushFailures.FirstOrDefault(f => f.TemplateId == id)?.ErrorMessage;

        return new MessageTemplateSyncOneResultDto(ToDto(refreshed), pushError);
    }

    /// <summary>Phase 1 of SyncWithMetaAsync - see IMessageTemplateService.SyncWithMetaAsync's doc
    /// comment for the full rule set. Active templates only: a deactivated template has no business
    /// occupying one of Meta's limited template slots or being resubmitted for review.
    /// <paramref name="onlyTemplateId"/> narrows this to a single template for SyncOneAsync (the
    /// active-only filter is skipped in that case - see SyncOneAsync's doc comment).</summary>
    private async Task<(int CandidateCount, int CreatedCount, int UpdatedCount, IReadOnlyList<TemplatePushFailureDto> Failures)> PushToMetaAsync(
        Guid? onlyTemplateId, CancellationToken cancellationToken)
    {
        var query = _context.MessageTemplates.AsQueryable();
        query = onlyTemplateId is { } id
            ? query.Where(t => t.Id == id)
            : query.Where(t => t.IsActive);
        query = query.Where(t => t.MetaTemplateId == null || t.BodyText != t.LastPushedBodyText);

        var candidates = await query.ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return (0, 0, 0, Array.Empty<TemplatePushFailureDto>());

        var createdCount = 0;
        var updatedCount = 0;
        var failures = new List<TemplatePushFailureDto>();
        var anyChange = false;

        foreach (var template in candidates)
        {
            var (metaBodyText, exampleValues) = TemplatePlaceholderResolver.ToMetaTemplateBody(template.BodyText);
            var submission = new WhatsAppTemplateSubmission(
                template.WhatsAppTemplateName, template.Language, template.Category.ToString(), metaBodyText, exampleValues);

            var result = template.MetaTemplateId is null
                ? await _whatsApp.CreateMessageTemplateAsync(submission, cancellationToken)
                : await _whatsApp.UpdateMessageTemplateAsync(template.MetaTemplateId, submission, cancellationToken);

            if (!result.Success)
            {
                failures.Add(new TemplatePushFailureDto(template.Id, template.WhatsAppTemplateName, result.ErrorMessage ?? "Unknown error."));
                continue;
            }

            var wasCreate = template.MetaTemplateId is null;
            template.MetaTemplateId = result.MetaTemplateId;
            template.LastPushedBodyText = template.BodyText;
            anyChange = true;

            if (wasCreate)
            {
                createdCount++;
                // Create's response often carries an immediate status (usually PENDING, occasionally
                // an instant APPROVED for simple templates) - applying it now means a freshly-created
                // template does not have to wait for this same job's pull phase, moments later, to
                // stop reading Pending from its own already-known local default.
                if (result.Status is not null)
                {
                    var (mappedStatus, mappedIsActive) = MapRemoteStatus(result.Status);
                    template.WhatsAppTemplateStatus = mappedStatus;
                    template.IsActive = mappedIsActive;
                }
            }
            else
            {
                updatedCount++;
            }
        }

        if (anyChange)
            await _context.SaveChangesAsync(cancellationToken);

        return (candidates.Count, createdCount, updatedCount, failures);
    }

    /// <summary>Phase 2 of SyncWithMetaAsync - pulls every template's current review status from Meta.
    /// See IMessageTemplateService.SyncWithMetaAsync's doc comment for the matching/mapping rules.
    /// <paramref name="onlyTemplateId"/> narrows local matching to a single template for SyncOneAsync -
    /// the remote list is still fetched in full (Meta has no single-template-by-name lookup that's
    /// cheaper than the list call), but only that one row can be updated locally.</summary>
    private async Task<(int RemoteCount, int MatchedCount, int StatusUpdatedCount, IReadOnlyList<string> Unmatched)> PullFromMetaAsync(
        Guid? onlyTemplateId, CancellationToken cancellationToken)
    {
        var remoteTemplates = await _whatsApp.GetMessageTemplatesAsync(cancellationToken);
        if (remoteTemplates.Count == 0)
            return (0, 0, 0, Array.Empty<string>());

        var localQuery = _context.MessageTemplates.AsQueryable();
        if (onlyTemplateId is { } id)
            localQuery = localQuery.Where(t => t.Id == id);
        var localTemplates = await localQuery.ToListAsync(cancellationToken);

        // Case-insensitive on both parts via TupleKeyComparer (a plain tuple's default equality is
        // case-sensitive): Meta conventionally lowercases template names, and a local row typed with
        // different casing should still be recognized as the same template rather than silently
        // never matching.
        var localByKey = localTemplates.ToDictionary(t => (t.WhatsAppTemplateName, t.Language), TupleKeyComparer.Instance);

        var matchedCount = 0;
        var statusUpdatedCount = 0;
        var unmatched = new List<string>();
        var anyChange = false;

        foreach (var remote in remoteTemplates)
        {
            if (!localByKey.TryGetValue((remote.Name, remote.Language), out var local))
            {
                unmatched.Add(remote.Name);
                continue;
            }

            matchedCount++;

            // Backfill, not a push-phase concern: a template Meta already had before this system ever
            // tried to create it (Meta's own "hello_world" sample template, or one entered directly in
            // Meta's UI) matches here by name/language but has no MetaTemplateId - without this, the
            // next push phase would keep attempting CreateMessageTemplateAsync for it and keep getting
            // "content already exists" forever. LastPushedBodyText is set to the current local body as
            // a best-effort assumption (this endpoint does not return component text, only metadata),
            // so a genuine future edit is still detected as a real push-worthy change rather than
            // comparing against a null baseline.
            if (local.MetaTemplateId is null)
            {
                local.MetaTemplateId = remote.Id;
                local.LastPushedBodyText = local.BodyText;
                anyChange = true;
            }

            var (mappedStatus, mappedIsActive) = MapRemoteStatus(remote.Status);

            if (local.WhatsAppTemplateStatus != mappedStatus || local.IsActive != mappedIsActive)
            {
                local.WhatsAppTemplateStatus = mappedStatus;
                local.IsActive = mappedIsActive;
                statusUpdatedCount++;
                anyChange = true;
            }
        }

        if (anyChange)
            await _context.SaveChangesAsync(cancellationToken);

        return (remoteTemplates.Count, matchedCount, statusUpdatedCount, unmatched);
    }

    /// <summary>See IMessageTemplateService.SyncWithMetaAsync's doc comment for the full mapping table
    /// this implements. IsActive is only ever forced to false here (a Rejected/Paused/Disabled
    /// template Meta will not deliver must not stay selectable) - never forced back to true, since a
    /// local IsActive=false could equally be a deliberate manual choice unrelated to Meta's status.</summary>
    private static (WhatsAppTemplateStatus Status, bool IsActive) MapRemoteStatus(string metaStatus) => metaStatus.ToUpperInvariant() switch
    {
        "APPROVED" => (WhatsAppTemplateStatus.Approved, true),
        "REJECTED" => (WhatsAppTemplateStatus.Rejected, false),
        "PENDING" or "IN_APPEAL" or "PENDING_DELETION" => (WhatsAppTemplateStatus.Pending, true),
        _ => (WhatsAppTemplateStatus.Rejected, false) // PAUSED, DISABLED, or any future Meta status.
    };

    /// <summary>Dictionary&lt;(string, string), T&gt; needs one IEqualityComparer for the tuple key to
    /// go case-insensitive on both components - ValueTuple's own default equality is case-sensitive.</summary>
    private class TupleKeyComparer : IEqualityComparer<(string Name, string Language)>
    {
        public static readonly TupleKeyComparer Instance = new();

        public bool Equals((string Name, string Language) x, (string Name, string Language) y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Language, y.Language, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, string Language) obj) =>
            HashCode.Combine(obj.Name.ToUpperInvariant(), obj.Language.ToUpperInvariant());
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var template = await FindOrThrowAsync(id, cancellationToken);

        var inUse = await _context.CampaignSteps.AnyAsync(s => s.MessageTemplateId == id, cancellationToken);
        if (inUse)
            throw new ConflictException($"Template '{template.Name}' is referenced by one or more campaign steps and cannot be deleted.");

        _context.MessageTemplates.Remove(template);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<MessageTemplate> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(MessageTemplate), id);

    private static MessageTemplateDto ToDto(MessageTemplate t) => new(
        t.Id, t.Name, t.Language, t.Category.ToString(), t.WhatsAppTemplateName,
        t.WhatsAppTemplateStatus.ToString(), t.BodyText, t.IsActive, t.CreatedAt, t.MetaTemplateId);
}
