using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Publishing: only approved answers go into a version; a version is an immutable snapshot with the evidence and who approved each answer;
/// answers needing another look (a changed source, a lapsed price, test data, an open conflict) are held back; a newer version supersedes
/// but never erases the old one; restoring is a new version; a changed or removed source marks the answers drawn from it.
/// </summary>
public sealed class KnowledgePublishingServiceTests : IDisposable
{
    private sealed class TenantScope : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class Admin : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Email => "owner@clinic.example";
        public IReadOnlyList<string> Roles => new[] { "Admin" };
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

    private sealed class Limits : ISourceLimitsProvider
    {
        public SourceLimits Get() => new(10, 50, 20, 50, 2, 15, 2048, true, true, true, 6, 20, 180);
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly Admin _admin = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private int _order;

    public KnowledgePublishingServiceTests()
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
                entry.Entity.CreatedAt = DateTime.UtcNow.AddSeconds(++tick);
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private KnowledgePublishingService Service() => new(_db, _admin);

    private KnowledgeVerificationService Verification() => new(_db, _admin, new Limits());

    /// <summary>A question with an approved answer and one quotation from a real passage of a source.</summary>
    private async Task<(KnowledgeQuestion Question, KnowledgeAnswer Answer, KnowledgeSource Source, SourcePassage Passage)> Approved(
        string summary = "It helps clinics save time and improve patient care.", bool material = false, Guid? tenant = null, KnowledgeSource? source = null)
    {
        var tenantId = tenant ?? _a;
        source ??= new KnowledgeSource { TenantId = tenantId, Name = "brochure.pdf", Status = KnowledgeSourceStatus.Ready, Version = 1 };
        if (_db.Entry(source).State == EntityState.Detached)
            _db.KnowledgeSources.Add(source);

        var text = $"{summary} (source text {++_order})";
        var passage = new SourcePassage { TenantId = tenantId, SourceId = source.Id, SourceVersion = source.Version, Order = _order, PageNumber = 2, Text = text, Hash = PassageSplitter.HashOf(text), ExtractedAt = DateTime.UtcNow };
        _db.SourcePassages.Add(passage);

        var question = new KnowledgeQuestion { TenantId = tenantId, Text = $"Question {_order}?", IsStandard = false, Category = "Benefits", SortOrder = _order };
        _db.KnowledgeQuestions.Add(question);
        var answer = new KnowledgeAnswer
        {
            TenantId = tenantId, QuestionId = question.Id, Version = 1, IsCurrent = true, Summary = summary, Detail = "- a\n- b", Confidence = KnowledgeConfidence.High, Kind = KnowledgeAnswerKind.SourceExtracted,
            Status = KnowledgeVerificationStatus.VerifiedAndApproved, ApprovedBy = _admin.UserId, ApprovedAt = DateTime.UtcNow, IsMaterialClaim = material, Provider = "OpenAI", GeneratedAt = DateTime.UtcNow,
            ReviewDueAt = material ? DateTime.UtcNow.AddDays(180) : null
        };
        _db.KnowledgeAnswers.Add(answer);
        _db.AnswerEvidences.Add(new AnswerEvidence
        {
            TenantId = tenantId, AnswerId = answer.Id, SourceId = source.Id, PassageId = passage.Id, SourceVersion = source.Version, Excerpt = summary, SourceName = source.Name, Location = "Page 2",
            PassageHash = passage.Hash
        });
        await _db.SaveChangesAsync();
        return (question, answer, source, passage);
    }

    // ----- publishing ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_approved_answers_are_published_as_an_immutable_snapshot_with_their_evidence_and_who_approved_them()
    {
        var (question, answer, _, _) = await Approved("The consultation fee is Rs 500.", material: true);
        var pending = new KnowledgeQuestion { TenantId = _a, Text = "Pending?", IsStandard = false, Category = "Custom" };
        _db.KnowledgeQuestions.Add(pending);
        _db.KnowledgeAnswers.Add(new KnowledgeAnswer { TenantId = _a, QuestionId = pending.Id, Summary = "A draft.", Provider = "OpenAI", IsCurrent = true, GeneratedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var preview = (await Service().GetAsync(_a)).Preview;
        Assert.Equal((1, 1, false, null), (preview.Ready, preview.NotYetApproved, preview.NothingNew, preview.Current));

        var version = await Service().PublishAsync(_a, new PublishRequest("First release."));

        Assert.Equal((1, "Current", 1, "owner@clinic.example", "First release."), (version.Number, version.Status, version.ItemCount, version.PublishedByEmail, version.Note));
        var detail = await Service().GetVersionAsync(_a, version.Id);
        var item = Assert.Single(detail.Items);
        Assert.Equal((question.Text, "Benefits", "The consultation fee is Rs 500.", true, "High"), (item.Question, item.Category, item.Summary, item.IsMaterialClaim, item.Confidence));
        Assert.Equal("- a\n- b", item.Detail);
        var evidence = Assert.Single(item.Evidence);
        Assert.Equal(("brochure.pdf", "Page 2"), (evidence.SourceName, evidence.Location));
        var stored = await _db.KnowledgeItems.SingleAsync();
        Assert.Equal((answer.Id, _admin.UserId), (stored.AnswerId, stored.ApprovedBy)); // who approved it, and which answer version
        Assert.NotNull(stored.ReviewDueAt);
    }

    [Fact]
    public async Task Nothing_approved_means_nothing_to_publish_and_publishing_twice_without_a_change_is_refused()
    {
        Assert.Contains("Nothing is approved yet", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, new PublishRequest(null)))).Message);

        await Approved();
        await Service().PublishAsync(_a, new PublishRequest(null));

        Assert.True((await Service().GetAsync(_a)).Preview.NothingNew);
        Assert.Contains("Nothing has changed since version 1", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, new PublishRequest(null)))).Message);
        Assert.Single(await _db.KnowledgeVersions.ToListAsync());
    }

    [Fact]
    public async Task A_new_version_supersedes_the_old_one_which_is_kept_exactly_as_it_was_published()
    {
        var (question, answer, _, _) = await Approved("It saves time.");
        var first = await Service().PublishAsync(_a, new PublishRequest(null));

        // The tenant edits the answer afterwards: a new answer version, approved.
        answer.IsCurrent = false;
        _db.KnowledgeAnswers.Add(new KnowledgeAnswer
        {
            TenantId = _a, QuestionId = question.Id, Version = 2, IsCurrent = true, Summary = "It saves an hour a day.", Kind = KnowledgeAnswerKind.TenantProvided, Provider = "Tenant",
            Status = KnowledgeVerificationStatus.VerifiedAndApproved, ApprovedBy = _admin.UserId, ApprovedAt = DateTime.UtcNow, GeneratedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        Assert.False((await Service().GetAsync(_a)).Preview.NothingNew);
        var second = await Service().PublishAsync(_a, new PublishRequest("Changed the benefit."));

        Assert.Equal(2, second.Number);
        var versions = (await Service().GetAsync(_a)).Versions;
        Assert.Equal(new[] { ("Current", 2), ("Superseded", 1) }, versions.Select(v => (v.Status, v.Number)));
        Assert.Equal("It saves time.", Assert.Single((await Service().GetVersionAsync(_a, first.Id)).Items).Summary); // untouched
        Assert.Equal("It saves an hour a day.", Assert.Single((await Service().GetVersionAsync(_a, second.Id)).Items).Summary);
    }

    // ----- held back --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_approved_answer_that_needs_another_look_is_held_back_with_the_reason_and_never_published()
    {
        var (_, ok, _, _) = await Approved("It saves time.");
        var (_, changed, _, _) = await Approved("It is fast.");
        var (_, lapsed, _, _) = await Approved("The fee is Rs 500.", material: true);
        var (_, testData, _, _) = await Approved("Placeholder.");
        var (_, disagree, _, _) = await Approved("The plan costs Rs 999.");
        changed.NeedsRevalidation = true;
        changed.RevalidationReason = "brochure.pdf changed: the words this answer was based on are no longer there as written.";
        lapsed.ReviewDueAt = DateTime.UtcNow.AddDays(-2);
        testData.IsSimulated = true;
        _db.KnowledgeConflicts.Add(new KnowledgeConflict { TenantId = _a, AnswerId = disagree.Id, Fact = "price", DetectedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var preview = (await Service().GetAsync(_a)).Preview;

        Assert.Equal(1, preview.Ready);
        Assert.Equal(new[] { "NeedsRecheck", "ReviewDue", "TestData", "OpenConflict" }, preview.HeldBack.Select(h => h.Code));
        Assert.Contains("no longer there as written", preview.HeldBack[0].Reason);
        Assert.Contains("lapsed", preview.HeldBack[1].Reason);

        var version = await Service().PublishAsync(_a, new PublishRequest(null));
        Assert.Equal(1, version.ItemCount);
        Assert.Equal(ok.Summary, Assert.Single(await _db.KnowledgeItems.ToListAsync()).Summary);
    }

    [Fact]
    public async Task When_everything_approved_is_held_back_the_message_says_to_look_at_it_again()
    {
        var (_, answer, _, _) = await Approved();
        answer.NeedsRevalidation = true;
        await _db.SaveChangesAsync();

        Assert.Contains("need another look", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, new PublishRequest(null)))).Message);
    }

    [Fact]
    public async Task Confirming_a_flagged_answer_again_clears_it_and_makes_it_publishable_and_a_lapsed_price_gets_a_new_review_date()
    {
        var (_, flagged, _, _) = await Approved("It saves time.");
        var (_, lapsed, _, _) = await Approved("The fee is Rs 500.", material: true);
        flagged.NeedsRevalidation = true;
        flagged.RevalidationReason = "brochure.pdf changed.";
        lapsed.ReviewDueAt = DateTime.UtcNow.AddDays(-1);
        await _db.SaveChangesAsync();

        await Verification().ApproveAsync(_a, flagged.Id, new ApproveAnswerRequest(false));
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Verification().ApproveAsync(_a, lapsed.Id, new ApproveAnswerRequest(false)));
        await Verification().ApproveAsync(_a, lapsed.Id, new ApproveAnswerRequest(true));

        Assert.False((await _db.KnowledgeAnswers.SingleAsync(a => a.Id == flagged.Id)).NeedsRevalidation);
        var renewed = await _db.KnowledgeAnswers.SingleAsync(a => a.Id == lapsed.Id);
        Assert.InRange((renewed.ReviewDueAt!.Value - DateTime.UtcNow).TotalDays, 179, 181);
        Assert.Contains("Confirmed again", (await _db.KnowledgeAnswerRevisions.Where(r => r.AnswerId == flagged.Id).SingleAsync()).Note);

        var preview = (await Service().GetAsync(_a)).Preview;
        Assert.Equal((2, 0), (preview.Ready, preview.HeldBack.Count));
    }

    [Fact]
    public async Task Approving_a_price_sets_its_review_date_and_an_ordinary_answer_has_none()
    {
        var question = new KnowledgeQuestion { TenantId = _a, Text = "Fee?", IsStandard = false, Category = "Custom" };
        _db.KnowledgeQuestions.Add(question);
        var answer = new KnowledgeAnswer { TenantId = _a, QuestionId = question.Id, Summary = "The fee is Rs 500.", IsMaterialClaim = true, Provider = "OpenAI", IsCurrent = true, GeneratedAt = DateTime.UtcNow, Confidence = KnowledgeConfidence.High };
        _db.KnowledgeAnswers.Add(answer);
        await _db.SaveChangesAsync();

        await Verification().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(true));

        Assert.InRange(((await _db.KnowledgeAnswers.SingleAsync()).ReviewDueAt!.Value - DateTime.UtcNow).TotalDays, 179, 181);
    }

    // ----- a source changes ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_source_read_again_marks_only_the_answers_whose_words_are_gone_and_changes_nothing_else()
    {
        var (_, kept, source, keptPassage) = await Approved("It saves time.");
        var (_, goes, _, goesPassage) = await Approved("It is fast.", source: source);

        // The source is read again: the first passage is unchanged, the second was rewritten.
        source.Version = 2;
        _db.SourcePassages.RemoveRange(_db.SourcePassages.Where(p => p.SourceId == source.Id));
        const string rewritten = "A completely different paragraph now.";
        _db.SourcePassages.AddRange(
            new SourcePassage { TenantId = _a, SourceId = source.Id, SourceVersion = 2, Order = 0, Text = keptPassage.Text, Hash = keptPassage.Hash, ExtractedAt = DateTime.UtcNow },
            new SourcePassage { TenantId = _a, SourceId = source.Id, SourceVersion = 2, Order = 1, Text = rewritten, Hash = PassageSplitter.HashOf(rewritten), ExtractedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var flagged = await KnowledgeRevalidation.MarkAffectedAsync(_db, _a, source.Id, sourceRemoved: false, CancellationToken.None);

        Assert.Equal(1, flagged);
        var stayed = await _db.KnowledgeAnswers.SingleAsync(a => a.Id == kept.Id);
        Assert.False(stayed.NeedsRevalidation);
        var hit = await _db.KnowledgeAnswers.SingleAsync(a => a.Id == goes.Id);
        Assert.True(hit.NeedsRevalidation);
        Assert.Contains("brochure.pdf changed", hit.RevalidationReason);
        Assert.Equal(KnowledgeVerificationStatus.VerifiedAndApproved, hit.Status); // the approval is not withdrawn - the tenant decides
        var step = await _db.KnowledgeAnswerRevisions.SingleAsync();
        Assert.Equal((KnowledgeRevisionKind.SourceChanged, goes.Id), (step.Kind, step.AnswerId));
        Assert.Equal(goesPassage.Id, (await _db.AnswerEvidences.SingleAsync(e => e.AnswerId == goes.Id)).PassageId); // the history is not erased

        // Running it again changes nothing.
        Assert.Equal(0, await KnowledgeRevalidation.MarkAffectedAsync(_db, _a, source.Id, false, CancellationToken.None));
        Assert.Single(await _db.KnowledgeAnswerRevisions.ToListAsync());
    }

    [Fact]
    public async Task A_removed_source_marks_every_answer_drawn_from_it_and_an_unrelated_source_marks_none()
    {
        var (_, one, source, _) = await Approved("It saves time.");
        var (_, two, _, _) = await Approved("It is fast.", source: source);
        var (_, other, otherSource, _) = await Approved("Other source answer.", source: new KnowledgeSource { TenantId = _a, Name = "site", Status = KnowledgeSourceStatus.Ready, Version = 1 });

        Assert.Equal(2, await KnowledgeRevalidation.MarkAffectedAsync(_db, _a, source.Id, sourceRemoved: true, CancellationToken.None));

        Assert.True((await _db.KnowledgeAnswers.SingleAsync(a => a.Id == one.Id)).NeedsRevalidation);
        Assert.True((await _db.KnowledgeAnswers.SingleAsync(a => a.Id == two.Id)).NeedsRevalidation);
        Assert.False((await _db.KnowledgeAnswers.SingleAsync(a => a.Id == other.Id)).NeedsRevalidation);
        Assert.Contains("was removed", (await _db.KnowledgeAnswers.SingleAsync(a => a.Id == one.Id)).RevalidationReason);
        Assert.Equal(0, await KnowledgeRevalidation.MarkAffectedAsync(_db, _a, Guid.NewGuid(), false, CancellationToken.None));
        Assert.NotEqual(otherSource.Id, source.Id);
    }

    [Fact]
    public async Task What_is_already_published_keeps_serving_after_its_source_changes_and_only_the_next_version_leaves_the_answer_out()
    {
        var (_, answer, source, _) = await Approved("It saves time.");
        await Approved("It is fast.", source: new KnowledgeSource { TenantId = _a, Name = "other.pdf", Status = KnowledgeSourceStatus.Ready, Version = 1 });
        var published = await Service().PublishAsync(_a, new PublishRequest(null));

        await KnowledgeRevalidation.MarkAffectedAsync(_db, _a, source.Id, sourceRemoved: true, CancellationToken.None);

        Assert.Equal(2, (await Service().GetVersionAsync(_a, published.Id)).Items.Count); // the published version is exactly as it was
        var next = await Service().PublishAsync(_a, new PublishRequest(null));
        Assert.Equal(1, next.ItemCount);
        Assert.DoesNotContain(await _db.KnowledgeItems.Where(i => i.VersionId == next.Id).ToListAsync(), i => i.AnswerId == answer.Id);
    }

    // ----- restoring -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Restoring_an_old_version_publishes_a_new_one_that_copies_it_and_nothing_in_between_is_erased()
    {
        var (question, answer, _, _) = await Approved("It saves time.");
        var v1 = await Service().PublishAsync(_a, new PublishRequest(null));
        answer.IsCurrent = false;
        _db.KnowledgeAnswers.Add(new KnowledgeAnswer
        {
            TenantId = _a, QuestionId = question.Id, Version = 2, IsCurrent = true, Summary = "It saves an hour.", Status = KnowledgeVerificationStatus.VerifiedAndApproved, Provider = "Tenant",
            ApprovedBy = _admin.UserId, ApprovedAt = DateTime.UtcNow, GeneratedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        var v2 = await Service().PublishAsync(_a, new PublishRequest(null));

        var v3 = await Service().RestoreAsync(_a, v1.Id, new PublishRequest(null));

        Assert.Equal((3, 1, "Restored from version 1."), (v3.Number, v3.RestoredFromNumber, v3.Note));
        Assert.Equal("It saves time.", Assert.Single((await Service().GetVersionAsync(_a, v3.Id)).Items).Summary);
        var all = (await Service().GetAsync(_a)).Versions;
        Assert.Equal(new[] { ("Current", 3), ("Superseded", 2), ("Superseded", 1) }, all.Select(v => (v.Status, v.Number)));
        Assert.Equal("It saves an hour.", Assert.Single((await Service().GetVersionAsync(_a, v2.Id)).Items).Summary);
        Assert.Contains("already the current version", (await Assert.ThrowsAsync<ConflictException>(() => Service().RestoreAsync(_a, v3.Id, new PublishRequest(null)))).Message);
    }

    // ----- isolation -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_versions_and_answers_are_never_seen_or_changed()
    {
        await Approved("Mine.");
        await Approved("Theirs.", tenant: _b);
        _tenant.TenantId = _b;
        var theirs = await Service().PublishAsync(_b, new PublishRequest(null));
        _tenant.TenantId = _a;

        var mine = await Service().GetAsync(_a);
        Assert.Empty(mine.Versions);
        Assert.Equal(1, mine.Preview.Ready);
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetVersionAsync(_a, theirs.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().RestoreAsync(_a, theirs.Id, new PublishRequest(null)));
        Assert.Equal(1, (await Service().PublishAsync(_a, new PublishRequest(null))).Number); // numbering is per tenant
    }

    [Fact]
    public void The_endpoints_are_tenant_admin_only_and_the_review_period_is_a_platform_setting()
    {
        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ProductKnowledgeVersionsController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Domain.Constants.AppRoles.Admin, authorize.Roles);
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));

        var setting = WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All.Single(d => d.Key == "ProductKnowledge:MaterialReviewDays");
        Assert.Equal("180", setting.DefaultValue);
    }
}
