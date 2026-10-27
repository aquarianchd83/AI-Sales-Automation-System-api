using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Planner;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Review;

/// <summary>
/// The gate between a script and the world. A draft is sent for approval only when it could honestly be approved (a picture for every scene,
/// a verified fact behind every claim); a person allowed to approve - an Admin, or a Sales Manager if the tenant's policy says so - approves it
/// or sends it back with a reason; approving freezes exactly what was looked at behind a fingerprint, and the claims are checked once more
/// against the facts as they are at that moment, so an approver cannot wave through what the facts no longer support. An approved or rejected
/// script is never edited: "revise" starts a new draft beside it. Every step is written to the script's review trail and to the tenant's audit
/// log, and nothing here deletes or rewrites an earlier entry.
/// </summary>
public sealed class ContentReviewService : IContentReviewService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _user;
    private readonly IDateTimeProvider _clock;
    private readonly IStudioSettingsService _settings;
    private readonly IAuditTrailWriter _audit;

    public ContentReviewService(IApplicationDbContext db, ICurrentUserService user, IDateTimeProvider clock, IStudioSettingsService settings, IAuditTrailWriter audit)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _settings = settings;
        _audit = audit;
    }

    // ----- reading ---------------------------------------------------------------------------------------------------------------

    public async Task<ReviewStateDto> GetAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var version = await FindAsync(tenantId, versionId, ct, track: false);
        var events = await _db.ContentReviewEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.VersionId == versionId).OrderBy(e => e.At).ThenBy(e => e.CreatedAt).ToListAsync(ct);
        var policy = (await _settings.GetAsync(tenantId, ct)).ApprovalPolicy;

        var integrity = true;
        if (version.Status == ContentVersionStatus.Approved)
        {
            var (scenes, assets) = await LoadAsync(tenantId, versionId, ct);
            integrity = !string.IsNullOrEmpty(version.ApprovedHash) && ContentSnapshot.Compute(scenes, assets) == version.ApprovedHash;
        }

        return new ReviewStateDto(
            version.Id, version.Status.ToString(), version.SubmittedAt, events.LastOrDefault(e => e.Action == ContentReviewAction.Submitted)?.ActorEmail, version.ReviewedAt,
            events.LastOrDefault(e => e.Action is ContentReviewAction.Approved or ContentReviewAction.Rejected)?.ActorEmail, version.ReviewComment,
            StudioApprovalRules.CanApprove(policy, _user.Roles), policy == StudioApprovalPolicy.AdminOnly, integrity,
            events.Select(e => new ReviewEventDto(e.Id, e.Action.ToString(), e.FromStatus?.ToString(), e.ToStatus.ToString(), e.ActorEmail, e.Comment, e.At)).ToList());
    }

    // ----- the steps -------------------------------------------------------------------------------------------------------------

    public async Task<ReviewStateDto> SubmitAsync(Guid tenantId, Guid versionId, ReviewActionRequest request, CancellationToken ct = default)
    {
        var version = await FindAsync(tenantId, versionId, ct);
        Require(version, ContentVersionStatus.Draft, "Only a draft can be sent for approval.");
        if (version.IsSimulated)
            throw new ConflictException("This script came from the platform's test mode, so it says nothing real about your business and cannot be sent for approval.");

        var (scenes, assets) = await LoadAsync(tenantId, versionId, ct, track: true);
        if (scenes.Count == 0)
            throw new ConflictException("This script has no scenes.");
        var without = scenes.Count(s => s.ChosenAssetId == null);
        if (without > 0)
            throw new ConflictException($"{without} of {scenes.Count} scenes have no picture yet. Choose a picture for every scene before sending the script for approval.");

        await RecheckClaimsAsync(tenantId, scenes, ct);
        var flagged = scenes.Count(s => s.ClaimStatus is ClaimStatus.Unverified or ClaimStatus.Unsupported);
        if (flagged > 0)
            throw new ConflictException($"{flagged} scene{(flagged == 1 ? " says" : "s say")} something none of your verified facts back. Reword {(flagged == 1 ? "it" : "them")}, or verify the fact, then send the script again.");

        var now = _clock.UtcNow;
        var from = version.Status;
        version.Status = ContentVersionStatus.InReview;
        version.SubmittedAt = now;
        version.SubmittedBy = _user.UserId;
        version.ReviewedAt = null;
        version.ReviewedBy = null;
        version.ReviewComment = null;
        version.UpdatedAt = now;
        AddEvent(version, ContentReviewAction.Submitted, from, Clean(request?.Comment), ContentSnapshot.Compute(scenes, assets));
        await SaveWithAuditAsync(version, from, ct);
        return await GetAsync(tenantId, versionId, ct);
    }

    public async Task<ReviewStateDto> WithdrawAsync(Guid tenantId, Guid versionId, ReviewActionRequest request, CancellationToken ct = default)
    {
        var version = await FindAsync(tenantId, versionId, ct);
        Require(version, ContentVersionStatus.InReview, "Only a script that is waiting for approval can be taken back.");

        var from = version.Status;
        version.Status = ContentVersionStatus.Draft;
        version.UpdatedAt = _clock.UtcNow;
        AddEvent(version, ContentReviewAction.Withdrawn, from, Clean(request?.Comment), string.Empty);
        await SaveWithAuditAsync(version, from, ct);
        return await GetAsync(tenantId, versionId, ct);
    }

    public async Task<ReviewStateDto> ApproveAsync(Guid tenantId, Guid versionId, ReviewActionRequest request, CancellationToken ct = default)
    {
        await EnsureMayApproveAsync(tenantId, ct);
        var version = await FindAsync(tenantId, versionId, ct);
        Require(version, ContentVersionStatus.InReview, "Only a script that is waiting for approval can be approved.");

        // The facts may have changed since it was sent: what is approved must be backed by what is true now.
        var (scenes, assets) = await LoadAsync(tenantId, versionId, ct, track: true);
        await RecheckClaimsAsync(tenantId, scenes, ct);
        var flagged = scenes.Count(s => s.ClaimStatus is ClaimStatus.Unverified or ClaimStatus.Unsupported);
        if (flagged > 0)
        {
            await _db.SaveChangesAsync(ct); // keep the fresh claim marks, so the sender sees which scenes
            throw new ConflictException($"{flagged} scene{(flagged == 1 ? " says" : "s say")} something your verified facts no longer back, so it cannot be approved. Send it back with a reason so it can be reworded.");
        }

        var now = _clock.UtcNow;
        var from = version.Status;
        var hash = ContentSnapshot.Compute(scenes, assets);
        version.Status = ContentVersionStatus.Approved;
        version.ApprovedHash = hash;
        version.ReviewedAt = now;
        version.ReviewedBy = _user.UserId;
        version.ReviewComment = Clean(request?.Comment);
        version.UpdatedAt = now;
        AddEvent(version, ContentReviewAction.Approved, from, version.ReviewComment, hash);
        await SaveWithAuditAsync(version, from, ct);
        return await GetAsync(tenantId, versionId, ct);
    }

    public async Task<ReviewStateDto> RejectAsync(Guid tenantId, Guid versionId, RejectRequest request, CancellationToken ct = default)
    {
        await EnsureMayApproveAsync(tenantId, ct);
        var reason = Clean(request?.Reason) ?? throw Invalid("reason", "Say why it is being sent back - the person who wrote it needs to know what to change.");
        var version = await FindAsync(tenantId, versionId, ct);
        Require(version, ContentVersionStatus.InReview, "Only a script that is waiting for approval can be sent back.");

        var now = _clock.UtcNow;
        var from = version.Status;
        version.Status = ContentVersionStatus.Rejected;
        version.ReviewedAt = now;
        version.ReviewedBy = _user.UserId;
        version.ReviewComment = reason;
        version.UpdatedAt = now;
        AddEvent(version, ContentReviewAction.Rejected, from, reason, string.Empty);
        await SaveWithAuditAsync(version, from, ct);
        return await GetAsync(tenantId, versionId, ct);
    }

    public async Task<Guid> ReviseAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var source = await FindAsync(tenantId, versionId, ct);
        if (source.Status is not (ContentVersionStatus.Approved or ContentVersionStatus.Rejected))
            throw new ConflictException(source.Status == ContentVersionStatus.Draft
                ? "This script is still a draft, so it can be edited directly."
                : "A script that is waiting for approval cannot be revised. Take it back first, or wait for the decision.");

        var scenes = await _db.ContentScenes.AsNoTracking().Where(s => s.TenantId == tenantId && s.VersionId == versionId).OrderBy(s => s.Order).ToListAsync(ct);
        var number = (await _db.ContentVersions.Where(v => v.TenantId == tenantId && v.ProjectId == source.ProjectId).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
        var now = _clock.UtcNow;

        var copy = new ContentVersion
        {
            TenantId = tenantId, ProjectId = source.ProjectId, Number = number, TopicId = source.TopicId, Status = ContentVersionStatus.Draft, ScriptText = source.ScriptText,
            TargetSeconds = source.TargetSeconds, Notes = source.Notes, PromptVersion = source.PromptVersion, Provider = source.Provider, Model = source.Model, IsSimulated = source.IsSimulated,
            ContentHash = source.ContentHash, CreatedBy = _user.UserId, RevisedFromNumber = source.Number
        };
        _db.ContentVersions.Add(copy);
        foreach (var scene in scenes)
            _db.ContentScenes.Add(new ContentScene
            {
                TenantId = tenantId, VersionId = copy.Id, Order = scene.Order, Narration = scene.Narration, OnScreenText = scene.OnScreenText, DurationSeconds = scene.DurationSeconds,
                VisualHint = scene.VisualHint, FactIdsJson = scene.FactIdsJson, ClaimStatus = scene.ClaimStatus, ClaimNote = scene.ClaimNote, ChosenAssetId = scene.ChosenAssetId,
                EditedByTenant = scene.EditedByTenant
            });

        var project = await _db.ContentProjects.FirstAsync(p => p.Id == source.ProjectId && p.TenantId == tenantId, ct);
        project.CurrentVersionId = copy.Id;
        project.UpdatedAt = now;
        _db.ContentReviewEvents.Add(new ContentReviewEvent
        {
            TenantId = tenantId, VersionId = copy.Id, Action = ContentReviewAction.Revised, FromStatus = null, ToStatus = ContentVersionStatus.Draft, ActorUserId = _user.UserId,
            ActorEmail = _user.Email, Comment = $"Started from version {source.Number} ({source.Status}).", SnapshotHash = string.Empty, At = now
        });
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(tenantId, nameof(ContentVersion), copy.Id, AuditAction.Create, new Dictionary<string, object?> { ["revisedFromVersion"] = source.Number, ["sourceStatus"] = source.Status.ToString() }, ct);
        return copy.Id;
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private async Task EnsureMayApproveAsync(Guid tenantId, CancellationToken ct)
    {
        var policy = (await _settings.GetAsync(tenantId, ct)).ApprovalPolicy;
        if (!StudioApprovalRules.CanApprove(policy, _user.Roles))
            throw new ForbiddenException(policy == StudioApprovalPolicy.AdminOnly
                ? "Only an Admin can approve content. Ask an Admin to review this script."
                : "You are not allowed to approve content.");
    }

    private void AddEvent(ContentVersion version, ContentReviewAction action, ContentVersionStatus from, string? comment, string hash) =>
        _db.ContentReviewEvents.Add(new ContentReviewEvent
        {
            TenantId = version.TenantId, VersionId = version.Id, Action = action, FromStatus = from, ToStatus = version.Status, ActorUserId = _user.UserId, ActorEmail = _user.Email,
            Comment = comment, SnapshotHash = hash, At = _clock.UtcNow
        });

    private async Task SaveWithAuditAsync(ContentVersion version, ContentVersionStatus from, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(version.TenantId, nameof(ContentVersion), version.Id, AuditAction.StatusChange,
            new Dictionary<string, object?> { ["from"] = from.ToString(), ["to"] = version.Status.ToString(), ["version"] = version.Number, ["comment"] = version.ReviewComment }, ct);
    }

    private async Task RecheckClaimsAsync(Guid tenantId, IReadOnlyList<ContentScene> scenes, CancellationToken ct)
    {
        var facts = await _db.BusinessFacts.IgnoreQueryFilters().AsNoTracking().Where(f => f.TenantId == tenantId).ToListAsync(ct);
        var now = _clock.UtcNow;
        foreach (var scene in scenes)
        {
            var outcome = PlannerClaims.Evaluate(scene.Narration, facts, now);
            scene.ClaimStatus = outcome.Status;
            scene.ClaimNote = outcome.Note;
        }
    }

    private async Task<(List<ContentScene> Scenes, Dictionary<Guid, StudioAsset> Assets)> LoadAsync(Guid tenantId, Guid versionId, CancellationToken ct, bool track = false)
    {
        var query = track ? _db.ContentScenes.AsQueryable() : _db.ContentScenes.AsNoTracking();
        var scenes = await query.Where(s => s.TenantId == tenantId && s.VersionId == versionId).OrderBy(s => s.Order).ToListAsync(ct);
        var ids = scenes.Where(s => s.ChosenAssetId != null).Select(s => s.ChosenAssetId!.Value).Distinct().ToList();
        var assets = ids.Count == 0 ? new Dictionary<Guid, StudioAsset>() : (await _db.StudioAssets.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.Id)).ToListAsync(ct)).ToDictionary(a => a.Id);
        return (scenes, assets);
    }

    private async Task<ContentVersion> FindAsync(Guid tenantId, Guid versionId, CancellationToken ct, bool track = true)
    {
        var query = track ? _db.ContentVersions.AsQueryable() : _db.ContentVersions.AsNoTracking();
        var version = await query.FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentVersion", versionId);
        if (!await _db.ContentProjects.AnyAsync(p => p.Id == version.ProjectId && p.TenantId == tenantId && !p.IsDeleted, ct))
            throw new NotFoundException("ContentVersion", versionId);
        return version;
    }

    private static void Require(ContentVersion version, ContentVersionStatus expected, string message)
    {
        if (version.Status != expected)
            throw new ConflictException($"{message} This script is {Describe(version.Status)}.");
    }

    private static string Describe(ContentVersionStatus status) => status switch
    {
        ContentVersionStatus.Draft => "a draft",
        ContentVersionStatus.InReview => "waiting for approval",
        ContentVersionStatus.Approved => "approved",
        _ => "rejected",
    };

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= 1000 ? trimmed : trimmed[..1000];
    }

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
