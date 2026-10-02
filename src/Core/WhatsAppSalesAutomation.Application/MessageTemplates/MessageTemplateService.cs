using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Notifications;
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
    /// <summary>Optional so a caller that never pushes a header image need not supply one; DI always does.</summary>
    private readonly IMediaStorageService? _mediaStorage;
    private readonly ITenantNotifier? _notifier;

    /// <summary>Meta's limits for an image header: JPEG or PNG, at most 5 MB.</summary>
    private const long MaxHeaderImageBytes = 5 * 1024 * 1024;
    private static readonly string[] HeaderImageContentTypes = { "image/jpeg", "image/png" };

    public MessageTemplateService(
        IApplicationDbContext context,
        IWhatsAppService whatsApp,
        IValidator<CreateMessageTemplateRequest> createValidator,
        IValidator<UpdateMessageTemplateRequest> updateValidator,
        IValidator<ReviewMessageTemplateRequest> reviewValidator,
        IMediaStorageService? mediaStorage = null,
        ITenantNotifier? notifier = null)
    {
        _mediaStorage = mediaStorage;
        _notifier = notifier;
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

        return new PagedResult<MessageTemplateDto>(await ToDtosAsync(items, cancellationToken), totalCount, request.Page, request.PageSize);
    }

    public async Task<MessageTemplateDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await ToDtoAsync(await FindOrThrowAsync(id, cancellationToken), cancellationToken);

    public async Task<MessageTemplateDto> CreateAsync(CreateMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var exists = await _context.MessageTemplates.AnyAsync(
            t => t.WhatsAppTemplateName == request.WhatsAppTemplateName && t.Language == request.Language,
            cancellationToken);
        if (exists)
            throw new ConflictException($"A template named '{request.WhatsAppTemplateName}' already exists for language '{request.Language}'.");

        if (request.HeaderMediaAssetId is { } headerId)
            await EnsureUsableHeaderImageAsync(headerId, cancellationToken);

        var template = new MessageTemplate
        {
            Name = request.Name,
            Language = request.Language,
            Category = Enum.Parse<TemplateCategory>(request.Category, ignoreCase: true),
            WhatsAppTemplateName = request.WhatsAppTemplateName,
            BodyText = request.BodyText,
            HeaderMediaAssetId = request.HeaderMediaAssetId,
            WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending
        };

        _context.MessageTemplates.Add(template);
        await _context.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(template, cancellationToken);
    }

    public async Task<MessageTemplateDto> UpdateAsync(Guid id, UpdateMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var template = await FindOrThrowAsync(id, cancellationToken);

        var newName = request.WhatsAppTemplateName ?? template.WhatsAppTemplateName;
        var newLanguage = request.Language ?? template.Language;
        var newCategory = request.Category is null
            ? template.Category
            : Enum.Parse<TemplateCategory>(request.Category, ignoreCase: true);

        var nameChanged = newName != template.WhatsAppTemplateName;
        var languageChanged = newLanguage != template.Language;
        var categoryChanged = newCategory != template.Category;

        if (nameChanged || languageChanged || categoryChanged)
        {
            // Meta does not allow changing a template's name, language or category after creation -
            // once MetaTemplateId is set, this system mirrors that constraint rather than silently
            // drifting the local copy out of sync with what Meta actually has, which
            // SendTemplateMessageAsync relies on matching exactly.
            if (template.MetaTemplateId is not null)
            {
                var changedFields = new[]
                {
                    nameChanged ? "name" : null,
                    languageChanged ? "language" : null,
                    categoryChanged ? "category" : null
                }.Where(f => f is not null).ToList();

                if (changedFields.Count == 1 && categoryChanged)
                    throw new ConflictException(
                        $"'{template.WhatsAppTemplateName}' is on Meta, and Meta assigns its category (it may reclassify a template on review). " +
                        "Use Sync to bring Meta's category here; to get a different one, create a new template in that category.");

                throw new ConflictException(
                    $"'{template.WhatsAppTemplateName}' has already been created on Meta and its {string.Join("/", changedFields)} cannot be changed there - create a new template instead.");
            }

            if (nameChanged || languageChanged)
            {
                var keyInUse = await _context.MessageTemplates.AnyAsync(
                    t => t.Id != id && t.WhatsAppTemplateName == newName && t.Language == newLanguage,
                    cancellationToken);
                if (keyInUse)
                    throw new ConflictException($"A template named '{newName}' already exists for language '{newLanguage}'.");
            }

            template.WhatsAppTemplateName = newName;
            template.Language = newLanguage;
            template.Category = newCategory;
        }

        // Editing the wording, language or category of an already-approved template is exactly what
        // Meta requires a fresh review for - a change to Approved content silently staying Approved
        // would let an unreviewed message go out under an approved template's name.
        if (template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved
            && (request.BodyText != template.BodyText || languageChanged || categoryChanged))
            template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;

        await ApplyHeaderImageChangeAsync(template, request, cancellationToken);

        template.BodyText = request.BodyText;
        template.IsActive = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(template, cancellationToken);
    }

    /// <summary>Which image a template shows can change any time - a send supplies the current one by link - but
    /// whether it HAS an image header is decided when Meta creates it and cannot change afterwards. So adding or
    /// removing the image is only allowed while the template is not on Meta yet; swapping it for another image is
    /// allowed on a template that already has an image header there.</summary>
    private async Task ApplyHeaderImageChangeAsync(MessageTemplate template, UpdateMessageTemplateRequest request, CancellationToken cancellationToken)
    {
        var newHeaderId = request.RemoveHeaderImage ? null : request.HeaderMediaAssetId ?? template.HeaderMediaAssetId;
        if (newHeaderId == template.HeaderMediaAssetId)
            return;

        if (template.MetaTemplateId is not null)
        {
            var addsOrRemoves = newHeaderId is null || template.HeaderMediaAssetId is null;
            if (addsOrRemoves || !template.HeaderOnMeta)
                throw new ConflictException(
                    $"'{template.WhatsAppTemplateName}' is already on Meta {(template.HeaderOnMeta ? "with" : "without")} an image, and Meta fixes that when a template is created. " +
                    "You can swap the image of a template that has one, but to add or remove the image create a new template.");
        }

        if (newHeaderId is { } id)
            await EnsureUsableHeaderImageAsync(id, cancellationToken);

        template.HeaderMediaAssetId = newHeaderId;
    }

    private async Task EnsureUsableHeaderImageAsync(Guid mediaAssetId, CancellationToken cancellationToken)
    {
        var asset = await _context.MediaAssets.FirstOrDefaultAsync(m => m.Id == mediaAssetId, cancellationToken)
            ?? throw new NotFoundException(nameof(Domain.Entities.Media.MediaAsset), mediaAssetId);

        if (!HeaderImageContentTypes.Contains(asset.ContentType, StringComparer.OrdinalIgnoreCase))
            throw new ConflictException($"'{asset.FileName}' can't be a template image: Meta accepts JPEG or PNG for a message header.");

        if (asset.SizeBytes > MaxHeaderImageBytes)
            throw new ConflictException($"'{asset.FileName}' is too large for a template image: Meta allows at most 5 MB.");
    }

    /// <summary>The header sample Meta needs when a template with an image is created or edited there, or null when
    /// the template has none to send. A missing file surfaces as a push failure, not an exception.</summary>
    private async Task<(WhatsAppTemplateHeaderImage? Image, string? Error)> LoadHeaderImageAsync(MessageTemplate template, CancellationToken cancellationToken)
    {
        if (template.HeaderMediaAssetId is not { } id)
            return (null, null);

        // On Meta already: only a template created with an image keeps sending one on an edit.
        if (template.MetaTemplateId is not null && !template.HeaderOnMeta)
            return (null, null);

        var asset = await _context.MediaAssets.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (asset is null || _mediaStorage is null)
            return (null, "The template's image is no longer available.");

        try
        {
            await using var stream = await _mediaStorage.OpenReadAsync(asset.StorageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return (new WhatsAppTemplateHeaderImage(asset.FileName, asset.ContentType, buffer.ToArray()), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"The template's image '{asset.FileName}' could not be read: {ex.Message}");
        }
    }

    public async Task<MessageTemplateDto> ReviewAsync(Guid id, ReviewMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _reviewValidator.ValidateAndThrowAsync(request, cancellationToken);

        var template = await FindOrThrowAsync(id, cancellationToken);

        // Once a template exists on Meta, Meta is the only authority on whether it is approved: the hourly
        // sync (and the per-row Sync) pulls its real review status and writes it back over this field. A
        // manual "Approve" would look like it worked, then be reverted at the next pull - and until then a
        // campaign could try to send a template Meta has not approved, which Meta rejects. So refuse it
        // outright rather than accept a value that cannot stick.
        if (template.MetaTemplateId is not null)
            throw new ConflictException(
                $"'{template.WhatsAppTemplateName}' is on Meta, and Meta decides its review status - a manual change would be reverted by the next sync. " +
                "Use Sync to fetch Meta's current status; it turns Approved once Meta finishes its review.");

        template.WhatsAppTemplateStatus = Enum.Parse<WhatsAppTemplateStatus>(request.Status, ignoreCase: true);

        await _context.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(template, cancellationToken);
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

        return new MessageTemplateSyncOneResultDto(await ToDtoAsync(refreshed, cancellationToken), pushError);
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
            var (headerImage, headerError) = await LoadHeaderImageAsync(template, cancellationToken);
            if (headerError is not null)
            {
                failures.Add(new TemplatePushFailureDto(template.Id, template.WhatsAppTemplateName, headerError));
                continue;
            }

            var submission = new WhatsAppTemplateSubmission(
                template.WhatsAppTemplateName, template.Language, template.Category.ToString(), metaBodyText, exampleValues, headerImage);

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
                // Created with its image header: from now on a send may attach the image, and Meta will not let the
                // template gain or lose the header.
                template.HeaderOnMeta = headerImage is not null;
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
        var reviewOutcomes = new List<(MessageTemplate Template, WhatsAppTemplateStatus Previous, string RemoteStatus, TemplateCategory PreviousCategory)>();

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

            var changed = false;
            var previousStatus = local.WhatsAppTemplateStatus;
            var previousCategory = local.Category;
            if (local.WhatsAppTemplateStatus != mappedStatus || local.IsActive != mappedIsActive)
            {
                local.WhatsAppTemplateStatus = mappedStatus;
                local.IsActive = mappedIsActive;
                changed = true;
            }

            // Meta may reclassify a template on review (e.g. a "Utility" one that breaks the utility
            // guidelines becomes Marketing). The category Meta holds is the one it bills and enforces, and it
            // also sets how many quota units each send uses here - so adopt it rather than keep a stale one.
            if (MapRemoteCategory(remote.Category) is { } remoteCategory && local.Category != remoteCategory)
            {
                local.Category = remoteCategory;
                changed = true;
            }

            if (changed)
            {
                statusUpdatedCount++;
                anyChange = true;
                reviewOutcomes.Add((local, previousStatus, remote.Status, previousCategory));
            }
        }

        if (anyChange)
            await _context.SaveChangesAsync(cancellationToken);

        // Told after the save, so the tenant is never told about a change that did not stick. Never throws.
        foreach (var (template, previous, remoteStatus, previousCategory) in reviewOutcomes)
            await NotifyReviewOutcomeAsync(template, previous, remoteStatus, previousCategory, cancellationToken);

        return (remoteTemplates.Count, matchedCount, statusUpdatedCount, unmatched);
    }

    /// <summary>Meta's category name to ours; null for one we do not model, which leaves the local category alone.</summary>
    private static TemplateCategory? MapRemoteCategory(string? metaCategory) => metaCategory?.ToUpperInvariant() switch
    {
        "MARKETING" => TemplateCategory.Marketing,
        "UTILITY" => TemplateCategory.Utility,
        "AUTHENTICATION" => TemplateCategory.Authentication,
        _ => null
    };

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

    /// <summary>Tells the tenant when Meta's review ended in something they must act on or can now use: approved (and whether
    /// Meta reclassified the category, which changes what each send costs), or rejected / paused / disabled (the template can no
    /// longer be sent). Moving back to Pending is the tenant's own edit or an appeal - not news.</summary>
    private async Task NotifyReviewOutcomeAsync(
        MessageTemplate template, WhatsAppTemplateStatus previous, string remoteStatus, TemplateCategory previousCategory, CancellationToken cancellationToken)
    {
        if (_notifier is null || template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Pending || template.WhatsAppTemplateStatus == previous)
            return;

        var approved = template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved;
        var kind = approved ? TenantNotificationKind.TemplateApproved : TenantNotificationKind.TemplateNeedsAttention;

        var outcome = remoteStatus.ToUpperInvariant() switch
        {
            "APPROVED" => "approved",
            "REJECTED" => "rejected",
            "PAUSED" => "paused",
            "DISABLED" => "disabled",
            _ => "not approved"
        };

        var title = approved ? $"Template \"{template.Name}\" was approved" : $"Template \"{template.Name}\" was {outcome} by Meta";
        var body = approved
            ? $"Meta approved \"{template.Name}\" - campaigns can use it now."
            : $"Meta {outcome} \"{template.Name}\", so it can't be sent and has been made inactive. Open Message Templates to review it, or make a copy and submit that.";

        if (approved && template.Category != previousCategory)
            body += $" Meta classed it as {template.Category} (you submitted {previousCategory}); that is the category it bills and counts against your quota under.";

        // Each review is its own episode (a template can be approved, paused and approved again), so the id alone would
        // swallow every notice after the first.
        var episode = $"{template.Id:N}-{template.WhatsAppTemplateStatus}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        await _notifier.NotifyAsync(new TenantNotificationRequest(
            template.TenantId, kind, null, episode, Truncate(title, 200), Truncate(body, 1000), AlsoWhatsApp: false), cancellationToken);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

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

    private async Task<MessageTemplateDto> ToDtoAsync(MessageTemplate t, CancellationToken cancellationToken) =>
        (await ToDtosAsync(new[] { t }, cancellationToken))[0];

    /// <summary>Maps templates, naming the image each one carries (and where the portal can preview it) with one
    /// lookup for the whole page, so the list can show WHICH image is attached without a request per row.</summary>
    private async Task<List<MessageTemplateDto>> ToDtosAsync(IReadOnlyCollection<MessageTemplate> templates, CancellationToken cancellationToken)
    {
        var ids = templates.Where(t => t.HeaderMediaAssetId != null).Select(t => t.HeaderMediaAssetId!.Value).Distinct().ToList();
        var assets = ids.Count == 0
            ? new Dictionary<Guid, (string FileName, string StorageKey, string Url, string Provider)>()
            : (await _context.MediaAssets.Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.FileName, a.StorageKey, a.Url, a.StorageProvider }).ToListAsync(cancellationToken))
                .ToDictionary(a => a.Id, a => (a.FileName, a.StorageKey, a.Url, Provider: a.StorageProvider));

        return templates.Select(t =>
        {
            string? fileName = null, url = null, preview = null;
            if (t.HeaderMediaAssetId is { } id && assets.TryGetValue(id, out var asset))
            {
                fileName = asset.FileName;
                url = Media.MediaAssetLinks.PublicUrl(asset.Provider, asset.StorageKey, asset.Url, _mediaStorage);
                preview = Media.MediaAssetLinks.PreviewUrl(asset.Provider, asset.StorageKey, asset.Url, _mediaStorage);
            }

            return new MessageTemplateDto(
                t.Id, t.Name, t.Language, t.Category.ToString(), t.WhatsAppTemplateName,
                t.WhatsAppTemplateStatus.ToString(), t.BodyText, t.IsActive, t.CreatedAt, t.MetaTemplateId,
                t.HeaderMediaAssetId, t.HeaderOnMeta, fileName, url, preview);
        }).ToList();
    }
}
