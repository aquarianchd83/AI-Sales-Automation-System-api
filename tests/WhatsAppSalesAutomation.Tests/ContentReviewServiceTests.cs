using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Api.Middleware;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Review;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The gate between a script and the world: a draft is sent for approval only when it could honestly be approved; only a person the tenant's
/// policy allows approves or rejects; approval re-checks the claims against the facts of that moment and freezes exactly what was looked at;
/// anything changed behind the review's back is detected; an approved or rejected script is never edited - a revision starts beside it; and
/// every step is recorded.
/// </summary>
public sealed class ContentReviewServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private sealed class TenantScope : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class Person : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Email { get; set; } = "owner@clinic.example";
        public IReadOnlyList<string> Roles { get; set; } = new[] { "Admin" };
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => Now;
        public DateTime IstNow => Now.AddHours(5.5);
    }

    private sealed class Settings : IStudioSettingsService
    {
        public StudioApprovalPolicy Policy { get; set; } = StudioApprovalPolicy.AdminOnly;
        public Task<StudioSettingsDto> GetAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(new StudioSettingsDto(Policy));
        public Task<StudioSettingsDto> UpdateAsync(Guid tenantId, UpdateStudioSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Audit : IAuditTrailWriter
    {
        public List<(string Entity, Guid Id, AuditAction Action, IReadOnlyDictionary<string, object?> Changes)> Rows { get; } = new();

        public Task RecordAsync(Guid tenantId, string entityName, Guid entityId, AuditAction action, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
        {
            Rows.Add((entityName, entityId, action, changes));
            return Task.CompletedTask;
        }
    }

    private const string OpenHours = "The clinic is open Monday to Saturday from 9 am to 6 pm.";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly Person _person = new();
    private readonly Settings _settings = new();
    private readonly Audit _audit = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public ContentReviewServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _tenant.TenantId = _a;
        _db = new SqliteApplicationDbContext(options, _tenant, new NoUser());
        _db.Database.EnsureCreated();
        var tick = 0;
        _db.BeforeSave = db =>
        {
            foreach (var entry in db.ChangeTracker.Entries<WhatsAppSalesAutomation.Domain.Common.BaseEntity>().Where(e => e.State == EntityState.Added))
                entry.Entity.CreatedAt = Now.AddSeconds(++tick);
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private ContentReviewService Service() => new(_db, _person, new Clock(), _settings, _audit);

    /// <summary>A draft with two scenes - one backed by a verified fact, one an invitation - each with a picture.</summary>
    private async Task<ContentVersion> Draft(Guid? tenant = null, bool pictures = true, bool simulated = false, string secondNarration = "Book your visit today.")
    {
        var tenantId = tenant ?? _a;
        if (!await _db.BusinessFacts.IgnoreQueryFilters().AnyAsync(f => f.TenantId == tenantId))
            _db.BusinessFacts.Add(new BusinessFact { TenantId = tenantId, Category = "Services", Statement = OpenHours, Status = BusinessFactStatus.Verified, VerifiedAtUtc = Now });
        var project = new ContentProject { TenantId = tenantId, Title = "Cataract awareness", Goal = "Bookings" };
        var version = new ContentVersion { TenantId = tenantId, ProjectId = project.Id, Number = 1, PromptVersion = "v", Provider = "OpenAI", ContentHash = new string('a', 64), IsSimulated = simulated };
        _db.ContentProjects.Add(project);
        _db.ContentVersions.Add(version);
        var narrations = new[] { "We are open Monday to Saturday from 9 am to 6 pm.", secondNarration };
        for (var i = 0; i < 2; i++)
        {
            var asset = new StudioAsset
            {
                TenantId = tenantId, Kind = StudioAssetKind.Upload, Name = $"Pic {i}", ContentType = "image/png", StorageKey = $"s3:k{i}", Checksum = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                LicenseName = "Provided by you", RightsConfirmed = true
            };
            _db.StudioAssets.Add(asset);
            _db.ContentScenes.Add(new ContentScene
            {
                TenantId = tenantId, VersionId = version.Id, Order = i + 1, Narration = narrations[i], OnScreenText = "Hello", DurationSeconds = 8, ChosenAssetId = pictures ? asset.Id : null,
                ClaimStatus = ClaimStatus.Supported
            });
        }

        await _db.SaveChangesAsync();
        return version;
    }

    private ContentVersion Fresh(Guid id) => _db.ContentVersions.AsNoTracking().IgnoreQueryFilters().Single(v => v.Id == id);

    private async Task Retire(string statement)
    {
        (await _db.BusinessFacts.IgnoreQueryFilters().FirstAsync(f => f.Statement == statement)).Status = BusinessFactStatus.Retired;
        await _db.SaveChangesAsync();
    }

    // ----- sending for approval --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_draft_that_could_honestly_be_approved_is_sent_with_who_and_when_and_the_step_is_recorded_twice()
    {
        var version = await Draft();

        var state = await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest("  Please check the opening hours.  "));

        Assert.Equal(("InReview", Now, "owner@clinic.example"), (state.Status, state.SubmittedAt, state.SubmittedByEmail));
        var stored = Fresh(version.Id);
        Assert.Equal((ContentVersionStatus.InReview, _person.UserId), (stored.Status, stored.SubmittedBy));
        var entry = Assert.Single(state.History);
        Assert.Equal(("Submitted", "Draft", "InReview", "Please check the opening hours."), (entry.Action, entry.FromStatus, entry.ToStatus, entry.Comment));
        var saved = await _db.ContentReviewEvents.SingleAsync();
        Assert.Equal(64, saved.SnapshotHash.Length); // what was looked at
        var audit = Assert.Single(_audit.Rows);
        Assert.Equal((nameof(ContentVersion), version.Id, AuditAction.StatusChange), (audit.Entity, audit.Id, audit.Action));
        Assert.Equal(("Draft", "InReview"), (audit.Changes["from"], audit.Changes["to"]));
    }

    [Fact]
    public async Task A_script_is_not_sent_unless_it_is_a_real_draft_with_a_picture_for_every_scene()
    {
        var noPictures = await Draft(pictures: false);
        Assert.Contains("2 of 2 scenes have no picture", (await Assert.ThrowsAsync<ConflictException>(() => Service().SubmitAsync(_a, noPictures.Id, new ReviewActionRequest(null)))).Message);

        var test = await Draft(simulated: true);
        Assert.Contains("test mode", (await Assert.ThrowsAsync<ConflictException>(() => Service().SubmitAsync(_a, test.Id, new ReviewActionRequest(null)))).Message);

        var ok = await Draft();
        await Service().SubmitAsync(_a, ok.Id, new ReviewActionRequest(null));
        var again = await Assert.ThrowsAsync<ConflictException>(() => Service().SubmitAsync(_a, ok.Id, new ReviewActionRequest(null)));
        Assert.Contains("Only a draft", again.Message);
        Assert.Contains("waiting for approval", again.Message);
    }

    [Fact]
    public async Task A_script_with_a_claim_no_verified_fact_backs_is_not_sent_and_the_marks_are_up_to_date()
    {
        var version = await Draft(secondNarration: "We have treated more than 10,000 patients.");

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null)));

        Assert.Contains("1 scene says something none of your verified facts back", ex.Message);
        Assert.Equal(ContentVersionStatus.Draft, Fresh(version.Id).Status);
        Assert.Empty(await _db.ContentReviewEvents.ToListAsync());
        Assert.Empty(_audit.Rows);
    }

    [Fact]
    public async Task The_claims_are_checked_afresh_when_sending_so_a_fact_retired_since_is_noticed()
    {
        var version = await Draft();
        await Retire(OpenHours);

        await Assert.ThrowsAsync<ConflictException>(() => Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null)));
    }

    [Fact]
    public async Task A_sender_can_take_a_script_back_to_change_it_and_send_it_again()
    {
        var version = await Draft();
        await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null));

        var state = await Service().WithdrawAsync(_a, version.Id, new ReviewActionRequest("Forgot to change the hours."));

        Assert.Equal("Draft", state.Status);
        Assert.Equal(new[] { "Submitted", "Withdrawn" }, state.History.Select(h => h.Action));
        await Assert.ThrowsAsync<ConflictException>(() => Service().WithdrawAsync(_a, version.Id, new ReviewActionRequest(null))); // only from review
        var again = await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null));
        Assert.Equal(new[] { "Submitted", "Withdrawn", "Submitted" }, again.History.Select(h => h.Action));
    }

    // ----- deciding --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_admin_approves_and_what_was_approved_is_frozen_behind_a_fingerprint_with_who_when_and_the_note()
    {
        var version = await Draft();
        await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null));
        _person.Email = "admin@clinic.example";

        var state = await Service().ApproveAsync(_a, version.Id, new ReviewActionRequest("  Looks right.  "));

        Assert.Equal(("Approved", Now, "admin@clinic.example", "Looks right.", true), (state.Status, state.ReviewedAt, state.ReviewedByEmail, state.ReviewComment, state.IntegrityOk));
        var stored = Fresh(version.Id);
        Assert.Equal(64, stored.ApprovedHash!.Length);
        var scenes = await _db.ContentScenes.AsNoTracking().Where(s => s.VersionId == version.Id).ToListAsync();
        var assets = (await _db.StudioAssets.AsNoTracking().ToListAsync()).ToDictionary(a => a.Id);
        Assert.Equal(ContentSnapshot.Compute(scenes, assets), stored.ApprovedHash);
        Assert.Equal(stored.ApprovedHash, (await _db.ContentReviewEvents.SingleAsync(e => e.Action == ContentReviewAction.Approved)).SnapshotHash);
        Assert.Equal(new[] { "Draft", "InReview" }, _audit.Rows.Select(r => r.Changes["from"]));
        Assert.Equal("Approved", _audit.Rows.Last().Changes["to"]);
    }

    [Fact]
    public async Task Who_may_approve_follows_the_tenants_policy_and_nobody_else_may()
    {
        var version = await Draft();
        await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null));
        _person.Roles = new[] { "SalesManager" };

        var denied = await Assert.ThrowsAsync<ForbiddenException>(() => Service().ApproveAsync(_a, version.Id, new ReviewActionRequest(null)));
        Assert.Contains("Only an Admin", denied.Message);
        await Assert.ThrowsAsync<ForbiddenException>(() => Service().RejectAsync(_a, version.Id, new RejectRequest("No.")));
        Assert.False((await Service().GetAsync(_a, version.Id)).CanApprove);
        Assert.True((await Service().GetAsync(_a, version.Id)).ApprovalIsAdminOnly);
        Assert.Equal(ContentVersionStatus.InReview, Fresh(version.Id).Status);

        _settings.Policy = StudioApprovalPolicy.AdminAndSalesManager;
        Assert.True((await Service().GetAsync(_a, version.Id)).CanApprove);
        Assert.Equal("Approved", (await Service().ApproveAsync(_a, version.Id, new ReviewActionRequest(null))).Status);

        // A role that is neither can never approve, whatever the policy.
        var other = await Draft();
        await Service().SubmitAsync(_a, other.Id, new ReviewActionRequest(null));
        _person.Roles = new[] { "SalesAgent" };
        await Assert.ThrowsAsync<ForbiddenException>(() => Service().ApproveAsync(_a, other.Id, new ReviewActionRequest(null)));
    }

    [Fact]
    public async Task Approval_checks_the_claims_against_the_facts_of_that_moment_and_cannot_wave_through_what_they_no_longer_support()
    {
        var version = await Draft();
        await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null));
        await Retire(OpenHours); // between sending and approving

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().ApproveAsync(_a, version.Id, new ReviewActionRequest(null)));

        Assert.Contains("no longer back", ex.Message);
        Assert.Equal(ContentVersionStatus.InReview, Fresh(version.Id).Status);
        Assert.Null(Fresh(version.Id).ApprovedHash);
        Assert.Equal(ClaimStatus.Unsupported, (await _db.ContentScenes.AsNoTracking().OrderBy(s => s.Order).FirstAsync()).ClaimStatus); // the sender can see which scene
        // It can still be sent back with a reason.
        Assert.Equal("Rejected", (await Service().RejectAsync(_a, version.Id, new RejectRequest("The opening hours fact was retired."))).Status);
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_which_is_kept_with_who_and_when()
    {
        var version = await Draft();
        await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null));

        await Assert.ThrowsAsync<ValidationException>(() => Service().RejectAsync(_a, version.Id, new RejectRequest("   ")));
        Assert.Equal(ContentVersionStatus.InReview, Fresh(version.Id).Status);

        var state = await Service().RejectAsync(_a, version.Id, new RejectRequest("  Too long for Reels.  "));

        Assert.Equal(("Rejected", "Too long for Reels.", Now), (state.Status, state.ReviewComment, state.ReviewedAt));
        Assert.Null(Fresh(version.Id).ApprovedHash);
        var last = state.History.Last();
        Assert.Equal(("Rejected", "InReview", "Too long for Reels."), (last.Action, last.FromStatus, last.Comment));
        await Assert.ThrowsAsync<ConflictException>(() => Service().ApproveAsync(_a, version.Id, new ReviewActionRequest(null))); // no longer in review
    }

    [Fact]
    public async Task Only_a_script_in_review_can_be_decided()
    {
        var draft = await Draft();

        Assert.Contains("waiting for approval", (await Assert.ThrowsAsync<ConflictException>(() => Service().ApproveAsync(_a, draft.Id, new ReviewActionRequest(null)))).Message);
        await Assert.ThrowsAsync<ConflictException>(() => Service().RejectAsync(_a, draft.Id, new RejectRequest("x")));
    }

    // ----- what was approved stays what was approved --------------------------------------------------------------------------------

    [Fact]
    public async Task Anything_changed_behind_the_reviews_back_is_detected_a_word_a_timing_or_a_different_picture()
    {
        var version = await Draft();
        await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null));
        await Service().ApproveAsync(_a, version.Id, new ReviewActionRequest(null));
        Assert.True((await Service().GetAsync(_a, version.Id)).IntegrityOk);

        var scene = await _db.ContentScenes.OrderBy(s => s.Order).FirstAsync();
        var original = scene.Narration;
        scene.Narration = original + " Free for everyone.";
        await _db.SaveChangesAsync();
        Assert.False((await Service().GetAsync(_a, version.Id)).IntegrityOk);

        scene.Narration = original;
        scene.DurationSeconds = 9;
        await _db.SaveChangesAsync();
        Assert.False((await Service().GetAsync(_a, version.Id)).IntegrityOk);

        scene.DurationSeconds = 8;
        await _db.SaveChangesAsync();
        Assert.True((await Service().GetAsync(_a, version.Id)).IntegrityOk);

        // The same picture row now holds different content: the checksum, not the name, is what was approved.
        (await _db.StudioAssets.FirstAsync(a => a.Id == scene.ChosenAssetId)).Checksum = new string('f', 64);
        await _db.SaveChangesAsync();
        Assert.False((await Service().GetAsync(_a, version.Id)).IntegrityOk);
    }

    [Fact]
    public void The_fingerprint_changes_with_a_word_a_length_or_a_picture_and_not_with_the_order_the_rows_come_in()
    {
        var assetA = new StudioAsset { Checksum = "aaa" };
        var assetB = new StudioAsset { Checksum = "bbb" };
        var assets = new Dictionary<Guid, StudioAsset> { [assetA.Id] = assetA, [assetB.Id] = assetB };
        ContentScene Scene(int order, string words, Guid? asset, int seconds = 8) => new() { Order = order, Narration = words, OnScreenText = "t", DurationSeconds = seconds, ChosenAssetId = asset };

        var baseline = ContentSnapshot.Compute(new[] { Scene(1, "One.", assetA.Id), Scene(2, "Two.", assetB.Id) }, assets);

        Assert.Equal(baseline, ContentSnapshot.Compute(new[] { Scene(2, "Two.", assetB.Id), Scene(1, "One.", assetA.Id) }, assets));
        Assert.NotEqual(baseline, ContentSnapshot.Compute(new[] { Scene(1, "One!", assetA.Id), Scene(2, "Two.", assetB.Id) }, assets));
        Assert.NotEqual(baseline, ContentSnapshot.Compute(new[] { Scene(1, "One.", assetA.Id, 9), Scene(2, "Two.", assetB.Id) }, assets));
        Assert.NotEqual(baseline, ContentSnapshot.Compute(new[] { Scene(1, "One.", assetB.Id), Scene(2, "Two.", assetB.Id) }, assets));
        Assert.NotEqual(baseline, ContentSnapshot.Compute(new[] { Scene(1, "One.", null), Scene(2, "Two.", assetB.Id) }, assets));
        Assert.Equal(64, baseline.Length);
    }

    // ----- revising --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revising_an_approved_script_starts_a_new_draft_beside_it_and_leaves_the_approved_one_exactly_as_it_was()
    {
        var version = await Draft();
        await Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null));
        await Service().ApproveAsync(_a, version.Id, new ReviewActionRequest("Good."));
        var approvedHash = Fresh(version.Id).ApprovedHash;

        var newId = await Service().ReviseAsync(_a, version.Id);

        var copy = Fresh(newId);
        Assert.Equal((ContentVersionStatus.Draft, 2, 1, null), (copy.Status, copy.Number, copy.RevisedFromNumber, copy.ApprovedHash));
        var scenes = await _db.ContentScenes.AsNoTracking().Where(s => s.VersionId == newId).OrderBy(s => s.Order).ToListAsync();
        var originals = await _db.ContentScenes.AsNoTracking().Where(s => s.VersionId == version.Id).OrderBy(s => s.Order).ToListAsync();
        Assert.Equal(originals.Select(s => (s.Narration, s.ChosenAssetId)), scenes.Select(s => (s.Narration, s.ChosenAssetId)));
        Assert.All(scenes, s => Assert.NotEqual(Guid.Empty, s.Id));
        Assert.Empty(scenes.Select(s => s.Id).Intersect(originals.Select(s => s.Id)));

        var original = Fresh(version.Id);
        Assert.Equal((ContentVersionStatus.Approved, approvedHash), (original.Status, original.ApprovedHash));
        Assert.True((await Service().GetAsync(_a, version.Id)).IntegrityOk);
        Assert.Equal(newId, (await _db.ContentProjects.SingleAsync()).CurrentVersionId);
        var history = await Service().GetAsync(_a, newId);
        Assert.Equal(("Revised", "Draft"), (history.History.Single().Action, history.History.Single().ToStatus));
        Assert.Contains("version 1 (Approved)", history.History.Single().Comment);
    }

    [Fact]
    public async Task A_rejected_script_can_be_revised_and_sent_again_through_the_same_gate()
    {
        var first = await Draft();
        await Service().SubmitAsync(_a, first.Id, new ReviewActionRequest(null));
        await Service().RejectAsync(_a, first.Id, new RejectRequest("Shorten it."));

        var second = await Service().ReviseAsync(_a, first.Id);
        await Service().SubmitAsync(_a, second, new ReviewActionRequest("Shortened."));
        var state = await Service().ApproveAsync(_a, second, new ReviewActionRequest(null));

        Assert.Equal("Approved", state.Status);
        Assert.Equal(ContentVersionStatus.Rejected, Fresh(first.Id).Status); // the old one keeps its verdict and its reason
        Assert.Equal("Shorten it.", Fresh(first.Id).ReviewComment);
    }

    [Fact]
    public async Task Only_an_approved_or_rejected_script_can_be_revised()
    {
        var draft = await Draft();
        Assert.Contains("still a draft", (await Assert.ThrowsAsync<ConflictException>(() => Service().ReviseAsync(_a, draft.Id))).Message);

        await Service().SubmitAsync(_a, draft.Id, new ReviewActionRequest(null));
        Assert.Contains("waiting for approval", (await Assert.ThrowsAsync<ConflictException>(() => Service().ReviseAsync(_a, draft.Id))).Message);
        Assert.Single(await _db.ContentVersions.ToListAsync());
    }

    // ----- isolation -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_scripts_are_never_seen_sent_decided_or_revised()
    {
        var mine = await Draft();
        await Service().SubmitAsync(_a, mine.Id, new ReviewActionRequest(null));
        _tenant.TenantId = _b;

        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetAsync(_b, mine.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().SubmitAsync(_b, mine.Id, new ReviewActionRequest(null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().WithdrawAsync(_b, mine.Id, new ReviewActionRequest(null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ApproveAsync(_b, mine.Id, new ReviewActionRequest(null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().RejectAsync(_b, mine.Id, new RejectRequest("x")));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ReviseAsync(_b, mine.Id));
        _tenant.TenantId = _a;

        Assert.Equal(ContentVersionStatus.InReview, Fresh(mine.Id).Status);
        Assert.Single(await _db.ContentReviewEvents.ToListAsync());
    }

    [Fact]
    public async Task A_deleted_projects_script_is_not_found()
    {
        var version = await Draft();
        (await _db.ContentProjects.SingleAsync()).IsDeleted = true;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => Service().SubmitAsync(_a, version.Id, new ReviewActionRequest(null)));
    }

    [Fact]
    public void The_endpoints_are_for_admins_and_managers_and_a_refusal_to_approve_is_a_403()
    {
        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ContentStudioReviewController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Api.Controllers.StudioRoles.Contributors, authorize.Roles);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(WhatsAppSalesAutomation.Api.Filters.RequiresFeatureAttribute)));
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));
    }

    [Fact]
    public async Task A_forbidden_exception_is_answered_with_403_and_its_message()
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var middleware = new ExceptionHandlingMiddleware(_ => throw new ForbiddenException("Only an Admin can approve content."), NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Contains("Only an Admin can approve content.", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
