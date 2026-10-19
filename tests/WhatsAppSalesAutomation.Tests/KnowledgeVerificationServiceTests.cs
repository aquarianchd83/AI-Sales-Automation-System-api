using System.Text.Json;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The tenant's decisions about drafted answers, and the audit trail they leave: approval needs an explicit confirmation for prices and weak
/// answers, test data and answers with open conflicts cannot be approved, an approval is never overwritten (an edit is a new version and the
/// approved one stays in the history), conflicts are settled only by the tenant's choice, and another tenant's answers are not found.
/// </summary>
public sealed class KnowledgeVerificationServiceTests : IDisposable
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

    private sealed class Routing : IAiFeatureRouting
    {
        public AiFeatureChoice Choice { get; set; } = new(true, "OpenAI");
        public AiFeatureChoice Resolve(string feature) => Choice;
    }

    private sealed class Limits : ISourceLimitsProvider
    {
        public SourceLimits Get() => new(10, 50, 20, 50, 2, 15, 2048, true, true, true, 6, 20);
    }

    private sealed class Queue : IKnowledgeDraftQueue
    {
        public void Enqueue(Guid runId, Guid tenantId) { }
    }

    private sealed class Ai : IAiTaskRunner
    {
        public Func<AiTaskRequest, string> Json { get; set; } = _ => "{}";

        public Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiTaskResult(true, Json(request), AiExecutionMode.Live, "OpenAI", "gpt", false, null, null, Guid.NewGuid(), Array.Empty<AiAttempt>(), 1, 1, 0m));
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly Admin _admin = new();
    private readonly Routing _routing = new();
    private readonly Ai _ai = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public KnowledgeVerificationServiceTests()
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

    private KnowledgeVerificationService Service() => new(_db, _admin);

    private KnowledgeDraftingService Drafting() => new(_db, _ai, _routing, new Limits(), new Queue(), NullLogger<KnowledgeDraftingService>.Instance);

    private async Task<(KnowledgeQuestion Question, KnowledgeAnswer Answer)> Draft(
        string? summary = "It helps clinics save time and improve patient care.", KnowledgeConfidence confidence = KnowledgeConfidence.High, bool material = false,
        bool unsupported = false, bool simulated = false, Guid? tenant = null)
    {
        var tenantId = tenant ?? _a;
        var question = new KnowledgeQuestion { TenantId = tenantId, Text = "What is the main benefit of your product?", Key = null, IsStandard = false, Category = "Custom" };
        _db.KnowledgeQuestions.Add(question);
        var answer = new KnowledgeAnswer
        {
            TenantId = tenantId, QuestionId = question.Id, Version = 1, IsCurrent = true, Summary = summary, Confidence = confidence, Provider = simulated ? "Simulator" : "OpenAI",
            IsSimulated = simulated, IsMaterialClaim = material, HasUnsupportedClaim = unsupported, GeneratedAt = DateTime.UtcNow, Kind = KnowledgeAnswerKind.AiInterpreted
        };
        _db.KnowledgeAnswers.Add(answer);
        await _db.SaveChangesAsync();
        return (question, answer);
    }

    private async Task<KnowledgeConflict> Conflict(KnowledgeAnswer answer, string fact = "price")
    {
        var conflict = new KnowledgeConflict
        {
            TenantId = answer.TenantId, AnswerId = answer.Id, Fact = fact, DetectedAt = DateTime.UtcNow,
            ValuesJson = JsonSerializer.Serialize(new[] { new ConflictValue("999", "brochure.pdf", "Page 2", "Costs Rs 999."), new ConflictValue("1499", "clinic.example", "/pricing", "Costs Rs 1,499.") },
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        answer.HasConflict = true;
        _db.KnowledgeConflicts.Add(conflict);
        await _db.SaveChangesAsync();
        return conflict;
    }

    // ----- approving ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_ordinary_answer_is_approved_with_who_and_when_and_the_step_is_recorded()
    {
        var (question, answer) = await Draft();

        var result = await Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(false));

        Assert.Equal("VerifiedAndApproved", result.Answer!.Status);
        Assert.NotNull(result.Answer.ApprovedAt);
        var stored = await _db.KnowledgeAnswers.SingleAsync(a => a.Id == answer.Id);
        Assert.Equal(_admin.UserId, stored.ApprovedBy);
        var step = Assert.Single(await _db.KnowledgeAnswerRevisions.Where(r => r.QuestionId == question.Id).ToListAsync());
        Assert.Equal(KnowledgeRevisionKind.Approved, step.Kind);
        Assert.Equal(("PendingVerification", "VerifiedAndApproved", "owner@clinic.example"), (step.FromStatus, step.ToStatus, step.ActorEmail));
    }

    [Theory]
    [InlineData(true, false, KnowledgeConfidence.High, "price, guarantee")]
    [InlineData(false, true, KnowledgeConfidence.Medium, "none of your sources states")]
    [InlineData(false, false, KnowledgeConfidence.Low, "weak")]
    public async Task A_price_an_unproven_figure_or_weak_evidence_needs_the_tenants_explicit_confirmation(bool material, bool unsupported, KnowledgeConfidence confidence, string reason)
    {
        var (_, answer) = await Draft(confidence: confidence, material: material, unsupported: unsupported);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(false)));
        Assert.Contains(ex.Errors, e => e.PropertyName == "confirmed" && e.ErrorMessage.Contains(reason));
        Assert.Equal(KnowledgeVerificationStatus.PendingVerification, (await _db.KnowledgeAnswers.SingleAsync(a => a.Id == answer.Id)).Status);

        var done = await Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(true));
        Assert.Equal("VerifiedAndApproved", done.Answer!.Status);
        Assert.Contains("Confirmed after review", (await _db.KnowledgeAnswerRevisions.SingleAsync()).Note);
    }

    [Fact]
    public async Task Test_data_and_an_answer_with_nothing_in_it_cannot_be_approved()
    {
        var (_, simulated) = await Draft(simulated: true);
        var (_, empty) = await Draft(summary: null, confidence: KnowledgeConfidence.NoEvidence);

        Assert.Contains("test data", (await Assert.ThrowsAsync<ConflictException>(() => Service().ApproveAsync(_a, simulated.Id, new ApproveAnswerRequest(true)))).Message);
        Assert.Contains("nothing to approve", (await Assert.ThrowsAsync<ConflictException>(() => Service().ApproveAsync(_a, empty.Id, new ApproveAnswerRequest(true)))).Message);
        Assert.Empty(await _db.KnowledgeAnswerRevisions.ToListAsync());
    }

    [Fact]
    public async Task An_answer_whose_sources_disagree_cannot_be_approved_until_the_tenant_has_chosen()
    {
        var (_, answer) = await Draft();
        var conflict = await Conflict(answer);

        Assert.Contains("disagree", (await Assert.ThrowsAsync<ConflictException>(() => Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(true)))).Message);

        var resolved = await Service().ResolveConflictAsync(_a, conflict.Id, new ResolveConflictRequest("1499", "The website is current."));
        Assert.False(resolved.Answer!.HasConflict);
        Assert.Equal("Resolved", Assert.Single(resolved.Answer.Conflicts).Status);

        Assert.Equal("VerifiedAndApproved", (await Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(false))).Answer!.Status);
    }

    [Fact]
    public async Task Approving_twice_records_one_approval_and_an_older_version_cannot_be_approved()
    {
        var (_, answer) = await Draft();
        await Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(false));
        await Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(false));

        Assert.Equal(1, await _db.KnowledgeAnswerRevisions.CountAsync(r => r.Kind == KnowledgeRevisionKind.Approved));

        var (_, old) = await Draft();
        old.IsCurrent = false;
        await _db.SaveChangesAsync();
        Assert.Contains("newer version", (await Assert.ThrowsAsync<ConflictException>(() => Service().ApproveAsync(_a, old.Id, new ApproveAnswerRequest(false)))).Message);
    }

    // ----- rejecting and reopening ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rejected_answer_keeps_the_reason_and_an_approved_one_must_be_reopened_first()
    {
        var (question, answer) = await Draft();

        var rejected = await Service().RejectAsync(_a, answer.Id, new RejectAnswerRequest("That is not how we describe it."));
        Assert.Equal("Rejected", rejected.Answer!.Status);
        Assert.Equal("That is not how we describe it.", (await _db.KnowledgeAnswerRevisions.SingleAsync(r => r.Kind == KnowledgeRevisionKind.Rejected)).Note);

        var (_, approved) = await Draft();
        await Service().ApproveAsync(_a, approved.Id, new ApproveAnswerRequest(false));
        Assert.Contains("Reopen", (await Assert.ThrowsAsync<ConflictException>(() => Service().RejectAsync(_a, approved.Id, new RejectAnswerRequest(null)))).Message);
        Assert.NotEqual(question.Id, Guid.Empty);
    }

    [Fact]
    public async Task Reopening_an_approved_answer_marks_it_for_correction_and_the_approval_stays_on_record()
    {
        var (question, answer) = await Draft();
        await Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(false));

        var reopened = await Service().ReopenAsync(_a, answer.Id, new ReopenAnswerRequest("Our pricing changed."));

        Assert.Equal("CorrectionRequired", reopened.Answer!.Status);
        Assert.NotNull(reopened.Answer.ApprovedAt); // who approved and when is not erased
        var history = await Service().HistoryAsync(_a, question.Id);
        Assert.Equal(new[] { "Reopened", "Approved" }, history.Select(h => h.Kind));
        Assert.Equal("Our pricing changed.", history[0].Note);

        var (_, pending) = await Draft();
        await Assert.ThrowsAsync<ConflictException>(() => Service().ReopenAsync(_a, pending.Id, new ReopenAnswerRequest(null)));
    }

    // ----- the tenant's own words ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_makes_a_new_approved_version_and_what_it_replaced_stays_in_the_history()
    {
        var (question, answer) = await Draft();
        await Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(false));

        var edited = await Service().EditAsync(_a, question.Id, new EditAnswerRequest("It saves clinic staff about an hour a day.", "- Faster records\n- Fewer errors", false));

        Assert.Equal(("TenantProvided", "VerifiedAndApproved", 2), (edited.Answer!.Kind, edited.Answer.Status, edited.Answer.Version));
        Assert.Equal("It saves clinic staff about an hour a day.", edited.Answer.Summary);
        var all = await _db.KnowledgeAnswers.Where(a => a.QuestionId == question.Id).OrderBy(a => a.Version).ToListAsync();
        Assert.Equal(new[] { false, true }, all.Select(a => a.IsCurrent));
        Assert.Equal("It helps clinics save time and improve patient care.", all[0].Summary); // the approved original is untouched
        Assert.Equal(KnowledgeVerificationStatus.VerifiedAndApproved, all[0].Status);
        Assert.Equal(_admin.UserId, all[1].ApprovedBy);

        var step = (await Service().HistoryAsync(_a, question.Id)).First();
        Assert.Equal("Edited", step.Kind);
        Assert.Equal(("It helps clinics save time and improve patient care.", "It saves clinic staff about an hour a day."), (step.FromText, step.ToText));
    }

    [Fact]
    public async Task What_the_sources_lacked_can_be_supplied_by_the_tenant_and_a_price_needs_their_confirmation()
    {
        var question = new KnowledgeQuestion { TenantId = _a, Text = "Do you offer a free trial?", IsStandard = false, Category = "Custom" };
        _db.KnowledgeQuestions.Add(question);
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Service().EditAsync(_a, question.Id, new EditAnswerRequest("Yes, a 14 day trial for Rs 0.", null, false)));
        Assert.Contains(ex.Errors, e => e.PropertyName == "confirmed");

        var made = await Service().EditAsync(_a, question.Id, new EditAnswerRequest("Yes, a 14 day trial for Rs 0.", null, true));

        Assert.Equal(1, made.Answer!.Version);
        Assert.True(made.Answer.IsMaterialClaim);
        Assert.Equal("InformationProvided", (await Service().HistoryAsync(_a, question.Id)).Single().Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public async Task An_answer_must_have_some_words_and_not_too_many(string summary)
    {
        var (question, _) = await Draft();

        await Assert.ThrowsAsync<ValidationException>(() => Service().EditAsync(_a, question.Id, new EditAnswerRequest(summary, null, true)));
        await Assert.ThrowsAsync<ValidationException>(() => Service().EditAsync(_a, question.Id, new EditAnswerRequest(new string('x', 2001), null, true)));
        await Assert.ThrowsAsync<ValidationException>(() => Service().EditAsync(_a, question.Id, new EditAnswerRequest("A fine summary.", new string('x', 8001), true)));
    }

    [Fact]
    public async Task Unknown_and_not_applicable_are_new_versions_with_no_answer_text_and_never_overwrite_an_approval()
    {
        var (question, answer) = await Draft();
        await Service().ApproveAsync(_a, answer.Id, new ApproveAnswerRequest(false));

        var unknown = await Service().MarkUnknownAsync(_a, question.Id);
        Assert.Equal(("InformationNotAvailable", null), (unknown.Answer!.Status, unknown.Answer.Summary));
        var notApplicable = await Service().MarkNotApplicableAsync(_a, question.Id);
        Assert.Equal("NotApplicable", notApplicable.Answer!.Status);
        await Service().MarkNotApplicableAsync(_a, question.Id); // repeating changes nothing

        Assert.Equal(3, await _db.KnowledgeAnswers.CountAsync(a => a.QuestionId == question.Id));
        Assert.Equal(KnowledgeVerificationStatus.VerifiedAndApproved, (await _db.KnowledgeAnswers.SingleAsync(a => a.Id == answer.Id)).Status);
        Assert.Equal(new[] { "MarkedNotApplicable", "MarkedUnknown", "Approved" }, (await Service().HistoryAsync(_a, question.Id)).Select(h => h.Kind));
    }

    // ----- conflicts -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_conflict_is_settled_only_by_the_tenants_choice_which_may_be_their_own_value_and_is_recorded()
    {
        var (question, answer) = await Draft();
        var first = await Conflict(answer);
        var second = await Conflict(answer, "duration");

        await Assert.ThrowsAsync<ValidationException>(() => Service().ResolveConflictAsync(_a, first.Id, new ResolveConflictRequest("  ", null)));
        var half = await Service().ResolveConflictAsync(_a, first.Id, new ResolveConflictRequest("Rs 1,299 from 1 April", "New price list."));
        Assert.True(half.Answer!.HasConflict); // one is still open

        Assert.Contains("already been decided", (await Assert.ThrowsAsync<ConflictException>(() => Service().ResolveConflictAsync(_a, first.Id, new ResolveConflictRequest("999", null)))).Message);
        var done = await Service().ResolveConflictAsync(_a, second.Id, new ResolveConflictRequest("6 months", null));
        Assert.False(done.Answer!.HasConflict);

        var stored = await _db.KnowledgeConflicts.SingleAsync(c => c.Id == first.Id);
        Assert.Equal(("Rs 1,299 from 1 April", _admin.UserId), (stored.ChosenValue, stored.ResolvedBy));
        Assert.NotNull(stored.ResolvedAt);
        var step = (await Service().HistoryAsync(_a, question.Id)).Last();
        Assert.Equal("ConflictResolved", step.Kind);
        Assert.Contains("Chose \"Rs 1,299 from 1 April\" for the price: New price list.", step.Note);
    }

    // ----- the trail ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_draft_is_the_first_entry_of_the_trail_with_what_the_application_found()
    {
        await _db.KnowledgeSources.AddAsync(new KnowledgeSource { TenantId = _a, Name = "brochure.pdf", Status = KnowledgeSourceStatus.Ready, Version = 1 });
        await _db.SaveChangesAsync();
        var source = await _db.KnowledgeSources.SingleAsync();
        const string text = "The consultation fee is Rs 500 for the first visit to the clinic.";
        _db.SourcePassages.Add(new SourcePassage { TenantId = _a, SourceId = source.Id, SourceVersion = 1, Text = text, Hash = PassageSplitter.HashOf(text), PageNumber = 1 });
        await _db.SaveChangesAsync();
        _ai.Json = _ => JsonSerializer.Serialize(new { answer = "The consultation fee is Rs 500.", insufficientEvidence = false, inference = false, evidence = new[] { new { passage = "P1", quote = "consultation fee is Rs 500" } } });

        var question = await Drafting().AskAsync(_a, null, new AskQuestionRequest("What is the consultation fee at the clinic?"));
        await Service().ApproveAsync(_a, question.Answer!.Id, new ApproveAnswerRequest(true));

        var history = await Service().HistoryAsync(_a, question.Id);
        Assert.Equal(new[] { "Approved", "Drafted" }, history.Select(h => h.Kind));
        Assert.Contains("OpenAI: High confidence, 1 verified quotation", history[1].Note);
        Assert.Null(history[1].ActorEmail); // the application drafted it
        Assert.Equal("owner@clinic.example", history[0].ActorEmail);
    }

    [Fact]
    public async Task A_run_leaves_a_not_applicable_question_alone_but_an_explicit_request_may_draft_it()
    {
        await _db.KnowledgeSources.AddAsync(new KnowledgeSource { TenantId = _a, Name = "brochure.pdf", Status = KnowledgeSourceStatus.Ready, Version = 1 });
        await _db.SaveChangesAsync();
        var source = await _db.KnowledgeSources.SingleAsync();
        const string text = "The software helps clinics save time and improve patient care, a key advantage for busy doctors.";
        _db.SourcePassages.Add(new SourcePassage { TenantId = _a, SourceId = source.Id, SourceVersion = 1, Text = text, Hash = PassageSplitter.HashOf(text), PageNumber = 1 });
        await _db.SaveChangesAsync();
        _ai.Json = _ => JsonSerializer.Serialize(new { answer = "It helps clinics save time.", insufficientEvidence = false, inference = false, evidence = new[] { new { passage = "P1", quote = "helps clinics save time and improve patient care" } } });

        var overview = await Drafting().GetAsync(_a);
        var main = overview.Questions.Single(q => q.Key == "main-benefit");
        await Service().MarkNotApplicableAsync(_a, main.Id);
        var run = await Drafting().StartRunAsync(_a, null);

        await Drafting().RunAsync(run.Id);

        Assert.True((await Drafting().GetRunAsync(_a, run.Id)).Skipped >= 1);
        Assert.Equal("NotApplicable", (await Drafting().GetAsync(_a)).Questions.Single(q => q.Key == "main-benefit").Answer!.Status);

        var again = await Drafting().RedraftAsync(_a, main.Id);
        Assert.Equal("PendingVerification", again.Answer!.Status);
    }

    [Fact]
    public async Task The_overview_counts_every_question_in_exactly_one_state()
    {
        var questions = new List<KnowledgeQuestionDto>();
        KnowledgeQuestionDto Q(string? status, KnowledgeConfidence confidence = KnowledgeConfidence.High) => new(
            Guid.NewGuid(), null, "q", false, "Custom", 0,
            status is null ? null : new KnowledgeAnswerDto(Guid.NewGuid(), 1, "s", null, "AiInterpreted", confidence.ToString(), status, null, null, Array.Empty<string>(), false, false, false, "OpenAI", null, false, DateTime.UtcNow,
                Array.Empty<AnswerEvidenceDto>(), Array.Empty<ConflictDto>()));
        questions.AddRange(new[]
        {
            Q("VerifiedAndApproved"), Q("VerifiedAndApproved"), Q("PendingVerification"), Q("CorrectionRequired"), Q("PendingVerification", KnowledgeConfidence.NoEvidence), Q(null),
            Q("Rejected"), Q("InformationNotAvailable"), Q("NotApplicable"),
        });

        var progress = KnowledgeDraftingService.ComputeProgress(questions);

        Assert.Equal(new AnswerProgressDto(9, 2, 2, 2, 1, 1, 1), progress);
        Assert.Equal(progress.Total, progress.Approved + progress.Waiting + progress.NeedsInformation + progress.Rejected + progress.Unknown + progress.NotApplicable);
    }

    // ----- one tenant never reaches another's ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_answers_questions_and_conflicts_are_not_found_by_any_decision_or_the_history()
    {
        var (theirQuestion, theirAnswer) = await Draft(tenant: _b);
        _db.KnowledgeConflicts.Add(new KnowledgeConflict { TenantId = _b, AnswerId = theirAnswer.Id, Fact = "price", DetectedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        var theirConflict = await _db.KnowledgeConflicts.IgnoreQueryFilters().SingleAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => Service().ApproveAsync(_a, theirAnswer.Id, new ApproveAnswerRequest(true)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().RejectAsync(_a, theirAnswer.Id, new RejectAnswerRequest(null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ReopenAsync(_a, theirAnswer.Id, new ReopenAnswerRequest(null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().EditAsync(_a, theirQuestion.Id, new EditAnswerRequest("Mine now.", null, true)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().MarkUnknownAsync(_a, theirQuestion.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().MarkNotApplicableAsync(_a, theirQuestion.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ResolveConflictAsync(_a, theirConflict.Id, new ResolveConflictRequest("1", null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().HistoryAsync(_a, theirQuestion.Id));
        Assert.Empty(await _db.KnowledgeAnswerRevisions.IgnoreQueryFilters().ToListAsync());
    }
}
