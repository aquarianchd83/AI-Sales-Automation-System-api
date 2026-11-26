using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Media;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public record PlatformMessageTemplateDto(
    Guid Id,
    string EventKey,
    string Name,
    string Language,
    string Category,
    string WhatsAppTemplateName,
    string Status,
    string BodyText,
    string DefaultBodyText,
    string SampleMessage,
    bool IsActive,
    string? MetaTemplateId,
    Guid? HeaderMediaAssetId,
    bool HeaderOnMeta,
    string? HeaderFileName,
    string? HeaderUrl,
    string? HeaderPreviewUrl,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? RejectionReason = null,
    string? RejectionTitle = null,
    string? RejectionTip = null,
    string? LastSyncError = null);

/// <param name="HeaderMediaAssetId">null keeps the current image; <paramref name="RemoveHeaderImage"/> clears it.</param>
public record UpdatePlatformMessageTemplateRequest(string Name, string BodyText, bool IsActive, Guid? HeaderMediaAssetId, bool RemoveHeaderImage);

public record SendPlatformTemplateTestRequest(string To);

/// <param name="MissingOnMeta">Templates this system thought were on Meta (they had a Meta id) but the WhatsApp Business Account does not hold -
/// deleted there, or the platform number was moved to another account. They are created again in the same run.</param>
public record PlatformTemplateSyncResultDto(
    bool Configured,
    string? Note,
    int Created,
    int Updated,
    IReadOnlyList<string> Failures,
    int RemoteCount,
    int StatusUpdated,
    int MissingOnMeta = 0);

public interface IPlatformMessageTemplateService
{
    /// <summary>One per kind of notice, in the order of the catalog.</summary>
    Task<IReadOnlyList<PlatformMessageTemplateDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<PlatformMessageTemplateDto> UpdateAsync(Guid id, UpdatePlatformMessageTemplateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Puts the seeded wording back. Counts as an edit: an approved template goes back to review.</summary>
    Task<PlatformMessageTemplateDto> RestoreDefaultAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Reads what Meta holds, creates every template that is not there (new, deleted on Meta, or the number moved to another WhatsApp
    /// Business Account), sends edited wording, then pulls Meta's review status. Never throws on a Meta error - it is in the result's failures.</summary>
    Task<PlatformTemplateSyncResultDto> SyncAsync(CancellationToken cancellationToken = default);

    Task<PlatformMessageTemplateDto> SyncOneAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Sends the template with sample values to a number, so the admin sees exactly what a tenant will. Only an approved template can go.</summary>
    Task<DeliveryTestResultDto> SendTestAsync(Guid id, string toPhone, CancellationToken cancellationToken = default);
}

public class PlatformMessageTemplateService : IPlatformMessageTemplateService
{
    private readonly IApplicationDbContext _context;
    private readonly IPlatformWhatsAppTemplateAdmin _admin;
    private readonly IPlatformWhatsAppSender _sender;
    private readonly IMediaStorageService _storage;
    private readonly IValidator<UpdatePlatformMessageTemplateRequest> _validator;

    public PlatformMessageTemplateService(
        IApplicationDbContext context,
        IPlatformWhatsAppTemplateAdmin admin,
        IPlatformWhatsAppSender sender,
        IMediaStorageService storage,
        IValidator<UpdatePlatformMessageTemplateRequest> validator)
    {
        _context = context;
        _admin = admin;
        _sender = sender;
        _storage = storage;
        _validator = validator;
    }

    public async Task<IReadOnlyList<PlatformMessageTemplateDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var templates = await _context.PlatformMessageTemplates.ToListAsync(cancellationToken);
        var order = PlatformTemplateCatalog.All.Select((d, i) => (Key: d.Kind.ToString(), Index: i)).ToDictionary(x => x.Key, x => x.Index);
        var ordered = templates.OrderBy(t => order.TryGetValue(t.EventKey, out var i) ? i : int.MaxValue).ThenBy(t => t.Name).ToList();
        return await ToDtosAsync(ordered, cancellationToken);
    }

    public async Task<PlatformMessageTemplateDto> UpdateAsync(Guid id, UpdatePlatformMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAsync(request, cancellationToken);
        var template = await FindOrThrowAsync(id, cancellationToken);

        // Editing an approved template's wording is what Meta wants a fresh review for; leaving it Approved would let unreviewed text go out
        // under an approved template's name.
        if (template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved && request.BodyText.Trim() != template.BodyText)
            template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;

        await ApplyHeaderImageChangeAsync(template, request.HeaderMediaAssetId, request.RemoveHeaderImage, cancellationToken);

        template.Name = request.Name.Trim();
        template.BodyText = request.BodyText.Trim();
        template.IsActive = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(new[] { template }, cancellationToken))[0];
    }

    public async Task<PlatformMessageTemplateDto> RestoreDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var template = await FindOrThrowAsync(id, cancellationToken);
        var definition = PlatformTemplateCatalog.For(template.EventKey)
            ?? throw new ConflictException($"'{template.Name}' has no default wording to restore.");

        if (template.BodyText != definition.Body && template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved)
            template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;

        template.Name = definition.Name;
        template.BodyText = definition.Body;

        await _context.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(new[] { template }, cancellationToken))[0];
    }

    /// <summary>Which image a template shows can change any time - a send supplies the current one by link - but whether it HAS an image header is
    /// fixed when Meta creates it. So adding or removing the image is only allowed while the template is not on Meta yet.</summary>
    private async Task ApplyHeaderImageChangeAsync(PlatformMessageTemplate template, Guid? requestedId, bool remove, CancellationToken cancellationToken)
    {
        var newHeaderId = remove ? null : requestedId ?? template.HeaderMediaAssetId;
        if (newHeaderId == template.HeaderMediaAssetId)
            return;

        if (template.MetaTemplateId is not null)
        {
            var addsOrRemoves = newHeaderId is null || template.HeaderMediaAssetId is null;
            if (addsOrRemoves || !template.HeaderOnMeta)
                throw new ConflictException(
                    $"'{template.WhatsAppTemplateName}' is already on Meta {(template.HeaderOnMeta ? "with" : "without")} an image or video, and Meta fixes that when a template is created. " +
                    "You can swap the image or video of a template that has one; to add or remove it, restore the default text and use a new template name.");
        }

        if (newHeaderId is { } id)
        {
            var asset = await _context.PlatformMediaAssets.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformMediaAsset), id);

            TemplateHeaderMedia.EnsureUsable(asset.FileName, asset.ContentType, asset.SizeBytes);

            // A swap on Meta must keep the kind (image or video) Meta created the template with.
            if (template.MetaTemplateId is not null && template.HeaderMediaAssetId is { } currentId)
            {
                var currentType = await _context.PlatformMediaAssets
                    .Where(m => m.Id == currentId)
                    .Select(m => m.ContentType)
                    .FirstOrDefaultAsync(cancellationToken);
                TemplateHeaderMedia.EnsureSameKind(template.WhatsAppTemplateName, currentType, asset.ContentType);
            }
        }

        template.HeaderMediaAssetId = newHeaderId;
    }

    public async Task<PlatformTemplateSyncResultDto> SyncAsync(CancellationToken cancellationToken = default) =>
        await SyncCoreAsync(null, cancellationToken);

    public async Task<PlatformMessageTemplateDto> SyncOneAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await FindOrThrowAsync(id, cancellationToken);
        var result = await SyncCoreAsync(id, cancellationToken);
        if (!result.Configured)
            throw new ConflictException(result.Note ?? "The platform WhatsApp number is not set up.");
        if (result.Failures.Count > 0)
            throw new ConflictException(result.Failures[0]);

        var refreshed = await FindOrThrowAsync(id, cancellationToken);
        return (await ToDtosAsync(new[] { refreshed }, cancellationToken))[0];
    }

    /// <summary>
    /// One sync, in three steps:
    /// 1. Read Meta's list and reconcile it with ours. A template Meta already has under the same name and language is adopted; one we hold a Meta
    ///    id for but Meta does not have any more is reset to "not on Meta" - otherwise it would keep its old id and old Approved status for ever,
    ///    the push below would skip it as already created, and Sync would silently do nothing (what happened after the platform number was moved
    ///    to a new WhatsApp Business Account: every template still carried the old account's ids).
    /// 2. Push: create what is not on Meta, send edited wording for what is.
    /// 3. Read Meta's list again (only if step 2 changed something) and take its review status and category.
    /// A list that cannot be read stops the run with Meta's reason as the failure, rather than being taken for "Meta has none of these".
    /// </summary>
    private async Task<PlatformTemplateSyncResultDto> SyncCoreAsync(Guid? onlyId, CancellationToken cancellationToken)
    {
        if (!await _admin.IsConfiguredAsync(cancellationToken))
            return new PlatformTemplateSyncResultDto(false, "The platform WhatsApp number is not set up (or is switched off), so nothing was sent to Meta.", 0, 0, Array.Empty<string>(), 0, 0);

        var remote = await _admin.GetTemplatesAsync(cancellationToken);
        if (remote.Error is not null)
            return new PlatformTemplateSyncResultDto(true, null, 0, 0, new[] { $"Could not read the templates on Meta: {remote.Error}" }, 0, 0);

        var local = await ScopeAsync(onlyId, cancellationToken);
        // Inactive templates are reset too (their old Meta id is just as wrong), but only the ones this run will push count as "sent again".
        var missing = Reconcile(local, remote.Templates, t => onlyId is not null || t.IsActive);
        await _context.SaveChangesAsync(cancellationToken);

        var (created, updated, failures) = await PushAsync(onlyId, cancellationToken);

        var statusUpdated = 0;
        if (created + updated > 0)
        {
            // The list read before the push is stale for what was just sent (an edited Approved template is back in review), so if the fresh
            // read fails, apply no status at all rather than the old one - the next sync or the hourly job picks it up.
            var after = await _admin.GetTemplatesAsync(cancellationToken);
            if (after.Error is null)
            {
                remote = after;
                statusUpdated = ApplyRemoteStatus(local, remote.Templates);
            }
            else
            {
                failures = failures.Append($"Sent to Meta, but its review status could not be read back: {after.Error}").ToList();
            }
        }
        else
        {
            statusUpdated = ApplyRemoteStatus(local, remote.Templates);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new PlatformTemplateSyncResultDto(true, null, created, updated, failures, remote.Templates.Count, statusUpdated, missing);
    }

    private async Task<List<PlatformMessageTemplate>> ScopeAsync(Guid? onlyId, CancellationToken cancellationToken)
    {
        var query = _context.PlatformMessageTemplates.AsQueryable();
        if (onlyId is { } id)
            query = query.Where(t => t.Id == id);
        return await query.ToListAsync(cancellationToken);
    }

    private static WhatsAppRemoteTemplate? FindRemote(PlatformMessageTemplate template, IReadOnlyList<WhatsAppRemoteTemplate> remote) =>
        remote.FirstOrDefault(r => string.Equals(template.WhatsAppTemplateName, r.Name, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(template.Language, r.Language, StringComparison.OrdinalIgnoreCase));

    /// <summary>Step 1 of a sync. Returns how many templates had a Meta id that Meta no longer knows.</summary>
    private static int Reconcile(IReadOnlyList<PlatformMessageTemplate> local, IReadOnlyList<WhatsAppRemoteTemplate> remote, Func<PlatformMessageTemplate, bool> counts)
    {
        var missing = 0;
        foreach (var template in local)
        {
            var match = FindRemote(template, remote);
            if (match is not null)
            {
                // Already on Meta before this system pushed it (entered by hand there, or pushed from another install): adopt it so the next
                // push edits it instead of failing to create a name that is taken.
                if (template.MetaTemplateId != match.Id)
                {
                    if (template.MetaTemplateId is null)
                        template.LastPushedBodyText = template.BodyText;
                    template.MetaTemplateId = match.Id;
                }
                continue;
            }

            if (template.MetaTemplateId is null)
                continue;

            // We hold an id Meta does not have: the template was deleted there, or the platform number now points at another WhatsApp Business
            // Account. Whatever status we had is not Meta's any more - start over as a new template.
            template.MetaTemplateId = null;
            template.LastPushedBodyText = null;
            template.HeaderOnMeta = false;
            template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;
            template.RejectionReason = null;
            if (counts(template))
                missing++;
        }

        return missing;
    }

    /// <summary>New templates are created on Meta and edited ones updated. Active templates only for the bulk run - a switched-off one has no
    /// business taking one of Meta's limited template slots - while an explicit sync of one row always goes.</summary>
    private async Task<(int Created, int Updated, IReadOnlyList<string> Failures)> PushAsync(Guid? onlyId, CancellationToken cancellationToken)
    {
        var query = _context.PlatformMessageTemplates.AsQueryable();
        query = onlyId is { } id ? query.Where(t => t.Id == id) : query.Where(t => t.IsActive);
        query = query.Where(t => t.MetaTemplateId == null || t.BodyText != t.LastPushedBodyText);

        var candidates = await query.ToListAsync(cancellationToken);
        var created = 0;
        var updated = 0;
        var failures = new List<string>();

        foreach (var template in candidates)
        {
            var sample = PlatformTemplateCatalog.For(template.EventKey)?.SampleMessage ?? "Your account has an update.";
            var (metaBody, examples) = TemplatePlaceholderResolver.ToMetaTemplateBody(template.BodyText, PlatformTemplateCatalog.Examples(sample));
            var (image, imageError) = await LoadHeaderImageAsync(template, cancellationToken);
            if (imageError is not null)
            {
                failures.Add($"{template.WhatsAppTemplateName}: {imageError}");
                template.LastSyncError = Truncate(imageError);
                continue;
            }

            var submission = new WhatsAppTemplateSubmission(
                template.WhatsAppTemplateName, template.Language, template.Category.ToString(), metaBody, examples, image);

            var wasCreate = template.MetaTemplateId is null;
            var result = wasCreate
                ? await _admin.CreateTemplateAsync(submission, cancellationToken)
                : await _admin.UpdateTemplateAsync(template.MetaTemplateId!, submission, cancellationToken);

            if (!result.Success)
            {
                failures.Add($"{template.WhatsAppTemplateName}: {result.ErrorMessage ?? "Unknown error."}");
                template.LastSyncError = Truncate(result.ErrorMessage ?? "Meta did not accept the template.");
                continue;
            }

            template.LastSyncError = null;
            template.MetaTemplateId = result.MetaTemplateId;
            template.LastPushedBodyText = template.BodyText;

            if (wasCreate)
            {
                created++;
                template.HeaderOnMeta = image is not null;
                template.WhatsAppTemplateStatus = result.Status is not null ? MapRemoteStatus(result.Status) : WhatsAppTemplateStatus.Pending;
            }
            else
            {
                updated++;
            }
        }

        if (candidates.Count > 0)
            await _context.SaveChangesAsync(cancellationToken);

        return (created, updated, failures);
    }

    /// <summary>Step 3 of a sync: Meta's review status and category onto every template it holds. Returns how many changed.</summary>
    private static int ApplyRemoteStatus(IReadOnlyList<PlatformMessageTemplate> local, IReadOnlyList<WhatsAppRemoteTemplate> remote)
    {
        var statusUpdated = 0;
        foreach (var template in local)
        {
            var r = FindRemote(template, remote);
            if (r is null)
                continue;

            if (template.MetaTemplateId is null)
            {
                template.MetaTemplateId = r.Id;
                template.LastPushedBodyText = template.BodyText;
            }

            var status = MapRemoteStatus(r.Status);
            var changed = template.WhatsAppTemplateStatus != status;
            template.WhatsAppTemplateStatus = status;

            // Only a rejection carries a reason; Meta answers "NONE" for everything else.
            var reason = status == WhatsAppTemplateStatus.Rejected ? MetaRejectionReasons.Normalize(r.RejectedReason) ?? r.Status.ToUpperInvariant() : null;
            if (template.RejectionReason != reason)
            {
                template.RejectionReason = reason;
                changed = true;
            }

            // Meta may reclassify on review; the category it holds is the one it bills.
            var category = MapRemoteCategory(r.Category);
            if (category is { } c && template.Category != c)
            {
                template.Category = c;
                changed = true;
            }

            if (changed)
                statusUpdated++;
        }

        return statusUpdated;
    }

    private async Task<(WhatsAppTemplateHeaderImage? Image, string? Error)> LoadHeaderImageAsync(PlatformMessageTemplate template, CancellationToken cancellationToken)
    {
        if (template.HeaderMediaAssetId is not { } id)
            return (null, null);

        // On Meta already: only a template created with an image keeps sending one on an edit.
        if (template.MetaTemplateId is not null && !template.HeaderOnMeta)
            return (null, null);

        var asset = await _context.PlatformMediaAssets.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (asset is null)
            return (null, "The template's image is no longer in the media library.");

        try
        {
            await using var stream = await _storage.OpenReadAsync(asset.StorageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return (new WhatsAppTemplateHeaderImage(asset.FileName, asset.ContentType, buffer.ToArray()), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"The template's image '{asset.FileName}' could not be read: {ex.Message}");
        }
    }

    public async Task<DeliveryTestResultDto> SendTestAsync(Guid id, string toPhone, CancellationToken cancellationToken = default)
    {
        if (!Auth.PhoneNumbers.TryNormalize(toPhone, out var phone))
            throw Problem("To", "Enter the WhatsApp number with the country code, for example +919876543210.");

        var template = await FindOrThrowAsync(id, cancellationToken);
        if (template.WhatsAppTemplateStatus != WhatsAppTemplateStatus.Approved)
            return new DeliveryTestResultDto(false, $"'{template.WhatsAppTemplateName}' is {template.WhatsAppTemplateStatus} on Meta. Sync it and wait for Meta to approve it before sending.");

        var sample = PlatformTemplateCatalog.For(template.EventKey)?.SampleMessage ?? "Your account has an update.";
        var parameters = PlatformNoticeTemplates.BuildParameters(template.BodyText, PlatformTemplateCatalog.SampleTenantName, PlatformTemplateCatalog.SampleTitle, sample);

        string? mediaUrl = null;
        if (template.HeaderOnMeta && template.HeaderMediaAssetId is { } assetId)
        {
            var asset = await _context.PlatformMediaAssets.FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
            if (asset is not null)
                mediaUrl = MediaAssetLinks.PublicUrl(asset.StorageProvider, asset.StorageKey, asset.Url, _storage);
        }

        var sent = await _sender.SendTemplateAsync(phone, template.WhatsAppTemplateName, template.Language, parameters, cancellationToken, mediaUrl);
        return sent.Success
            ? new DeliveryTestResultDto(true, $"Sent to {phone}.")
            : new DeliveryTestResultDto(false, sent.Note ?? "WhatsApp did not accept the message.");
    }

    /// <summary>Meta's status to ours. A paused or disabled template is as unsendable as a rejected one.</summary>
    internal static WhatsAppTemplateStatus MapRemoteStatus(string metaStatus) => metaStatus.ToUpperInvariant() switch
    {
        "APPROVED" => WhatsAppTemplateStatus.Approved,
        "PENDING" or "IN_APPEAL" or "PENDING_DELETION" => WhatsAppTemplateStatus.Pending,
        _ => WhatsAppTemplateStatus.Rejected
    };

    private static TemplateCategory? MapRemoteCategory(string? metaCategory) => metaCategory?.ToUpperInvariant() switch
    {
        "MARKETING" => TemplateCategory.Marketing,
        "UTILITY" => TemplateCategory.Utility,
        "AUTHENTICATION" => TemplateCategory.Authentication,
        _ => null
    };

    private async Task<PlatformMessageTemplate> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.PlatformMessageTemplates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(PlatformMessageTemplate), id);

    private async Task<List<PlatformMessageTemplateDto>> ToDtosAsync(IReadOnlyCollection<PlatformMessageTemplate> templates, CancellationToken cancellationToken)
    {
        var ids = templates.Where(t => t.HeaderMediaAssetId != null).Select(t => t.HeaderMediaAssetId!.Value).Distinct().ToList();
        var assets = ids.Count == 0
            ? new Dictionary<Guid, PlatformMediaAsset>()
            : (await _context.PlatformMediaAssets.Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken)).ToDictionary(a => a.Id);

        return templates.Select(t =>
        {
            string? fileName = null, url = null, preview = null;
            if (t.HeaderMediaAssetId is { } id && assets.TryGetValue(id, out var asset))
            {
                fileName = asset.FileName;
                url = MediaAssetLinks.PublicUrl(asset.StorageProvider, asset.StorageKey, asset.Url, _storage);
                preview = MediaAssetLinks.PreviewUrl(asset.StorageProvider, asset.StorageKey, asset.Url, _storage);
            }

            var definition = PlatformTemplateCatalog.For(t.EventKey);
            var rejection = t.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Rejected ? MetaRejectionReasons.Explain(t.RejectionReason) : null;
            return new PlatformMessageTemplateDto(
                t.Id, t.EventKey, t.Name, t.Language, t.Category.ToString(), t.WhatsAppTemplateName, t.WhatsAppTemplateStatus.ToString(),
                t.BodyText, definition?.Body ?? t.BodyText, definition?.SampleMessage ?? string.Empty, t.IsActive, t.MetaTemplateId,
                t.HeaderMediaAssetId, t.HeaderOnMeta, fileName, url, preview, t.CreatedAt, t.UpdatedAt,
                t.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Rejected ? t.RejectionReason : null, rejection?.Title, rejection?.Tip, t.LastSyncError);
        }).ToList();
    }

    private static string Truncate(string text) => text.Length <= 1000 ? text : text[..1000];

    private static ValidationException Problem(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}

public class UpdatePlatformMessageTemplateRequestValidator : AbstractValidator<UpdatePlatformMessageTemplateRequest>
{
    public UpdatePlatformMessageTemplateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);

        RuleFor(x => x.BodyText).NotEmpty().MaximumLength(PlatformTemplateCatalog.MaxBodyLength);

        RuleFor(x => x.BodyText)
            .Must(body => PlatformTemplateCatalog.UnknownTokens(body).Count == 0)
            .WithMessage(x => $"Unknown placeholder: {string.Join(", ", PlatformTemplateCatalog.UnknownTokens(x.BodyText).Select(t => "{{" + t + "}}"))}. " +
                              $"Use {string.Join(", ", PlatformTemplateCatalog.Tokens.Select(t => "{{" + t + "}}"))}.")
            .When(x => !string.IsNullOrWhiteSpace(x.BodyText));

        // Meta rejects a body that starts or ends with a variable.
        RuleFor(x => x.BodyText)
            .Must(body => !body.TrimStart().StartsWith("{{") && !body.TrimEnd().EndsWith("}}"))
            .WithMessage("Meta doesn't accept a message that starts or ends with a placeholder - add a few words before and after.")
            .When(x => !string.IsNullOrWhiteSpace(x.BodyText));
    }
}
