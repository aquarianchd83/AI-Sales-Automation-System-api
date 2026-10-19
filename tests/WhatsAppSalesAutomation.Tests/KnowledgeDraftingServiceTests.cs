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
/// Drafting answers from a tenant's sources, end to end against a database with a scripted AI: the answer is only as good as its verified
/// quotations; an invented quote, an unproven figure or a model that says too much is caught by the application, whatever the provider;
/// nothing is asked of the AI when no passage is relevant; the simulator's output is never an answer; approved answers are never replaced;
/// another tenant's sources and questions are invisible.
/// </summary>
public sealed class KnowledgeDraftingServiceTests : IDisposable
{
    private sealed class TenantScope : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
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
        public AiFeatureChoice Suggested { get; set; } = new(true, "OpenAI");
        public AiFeatureChoice Resolve(string feature) => feature == AiFeatures.ProductSuggestedAnswers ? Suggested : new AiFeatureChoice(true, "OpenAI");
    }

    private sealed class FixedLimits : ISourceLimitsProvider
    {
        public SourceLimits Value { get; set; } = new(10, 50, 20, 50, 2, 15, 2048, true, true, true, 6, 3);
        public SourceLimits Get() => Value;
    }

    private sealed class RecordingQueue : IKnowledgeDraftQueue
    {
        public List<(Guid RunId, Guid TenantId)> Queued { get; } = new();
        public void Enqueue(Guid runId, Guid tenantId) => Queued.Add((runId, tenantId));
    }

    private sealed class ScriptedAi : IAiTaskRunner
    {
        public List<AiTaskRequest> Calls { get; } = new();
        public Func<AiTaskRequest, AiTaskResult> Respond { get; set; } = _ => Failure("not scripted");

        public static AiTaskResult Live(string json) =>
            new(true, json, AiExecutionMode.Live, "OpenAI", "gpt-test", false, null, null, Guid.NewGuid(), Array.Empty<AiAttempt>(), 100, 50, 0.001m);

        public static AiTaskResult Simulated(string json) =>
            new(true, json, AiExecutionMode.Simulator, AiCapabilities.Simulator, "simulator", false, null, null, Guid.NewGuid(), Array.Empty<AiAttempt>(), null, null, null);

        public static AiTaskResult Failure(string message) =>
            new(false, null, AiExecutionMode.Live, "OpenAI", null, false, "PROVIDER_ERROR", message, Guid.NewGuid(), Array.Empty<AiAttempt>(), null, null, null);

        public Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly Routing _routing = new();
    private readonly FixedLimits _limits = new();
    private readonly RecordingQueue _queue = new();
    private readonly ScriptedAi _ai = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    private const string FeePassage = "The consultation fee is Rs 500 for the first visit to the clinic.";

    public KnowledgeDraftingServiceTests()
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

    private KnowledgeDraftingService Service() => new(_db, _ai, _routing, _limits, _queue, NullLogger<KnowledgeDraftingService>.Instance);

    private async Task<Guid> Source(string name, Guid tenant, params string[] passages)
    {
        var source = new KnowledgeSource { TenantId = tenant, Name = name, Kind = KnowledgeSourceKind.Document, Status = KnowledgeSourceStatus.Ready, Version = 1, PassageCount = passages.Length };
        _db.KnowledgeSources.Add(source);
        var order = 0;
        foreach (var text in passages)
            _db.SourcePassages.Add(new SourcePassage { TenantId = tenant, SourceId = source.Id, SourceVersion = 1, Order = order++, PageNumber = order, Text = text, Hash = PassageSplitter.HashOf(text), ExtractedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        return source.Id;
    }

    private static string Reply(string? answer, string passage = "P1", string? quote = null, bool insufficient = false, bool inference = false, string? detail = null, string? clarify = null) =>
        JsonSerializer.Serialize(new
        {
            answer, detail, insufficientEvidence = insufficient, inference, clarifyingQuestion = clarify, limitations = (string?)null,
            evidence = quote is null ? Array.Empty<object>() : new object[] { new { passage, quote } }
        });

    private async Task<KnowledgeQuestionDto> Ask(string text = "What is the consultation fee at the clinic?") =>
        await Service().AskAsync(_a, null, new AskQuestionRequest(text));

    // ----- a good answer ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_answer_that_a_passage_states_directly_is_high_with_the_passages_own_words_as_evidence_and_still_pending_verification()
    {
        await Source("brochure.pdf", _a, FeePassage, "Parking is free for patients.");
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 500 for the first visit.", quote: "consultation fee is rs 500 for the first visit"));

        var answer = (await Ask()).Answer!;

        Assert.Equal("High", answer.Confidence);
        Assert.Equal("PendingVerification", answer.Status); // confidence and verification are separate
        Assert.Equal("SourceExtracted", answer.Kind);
        Assert.Equal("The consultation fee is Rs 500 for the first visit.", answer.Summary);
        Assert.True(answer.IsMaterialClaim); // a price needs the tenant's explicit approval
        Assert.False(answer.HasUnsupportedClaim);
        Assert.Equal(("OpenAI", "gpt-test", false), (answer.Provider, answer.Model, answer.IsSimulated));
        var evidence = Assert.Single(answer.Evidence);
        Assert.Equal(("brochure.pdf", "Page 1"), (evidence.SourceName, evidence.Location));
        Assert.Equal("consultation fee is Rs 500 for the first visit", evidence.Excerpt); // cut from the passage, not the model's version
        Assert.Contains(answer.Reasons, r => r.Contains("1 verified quotation"));

        var call = Assert.Single(_ai.Calls);
        Assert.Equal(AiTaskKinds.CustomQuestionAnswering, call.Task);
        Assert.Equal(_a, call.TenantId);
        Assert.Contains("[P1] (brochure.pdf, Page 1)", call.User);
        Assert.DoesNotContain("Parking", call.User); // an irrelevant passage is not even sent
    }

    [Fact]
    public async Task The_standard_questions_use_the_suggested_answer_task_and_their_hint_words_to_find_passages()
    {
        await Source("brochure.pdf", _a, "The software helps clinics save time and improve patient care, a key advantage for busy doctors.");
        _ai.Respond = _ => ScriptedAi.Live(Reply("It helps clinics save time and improve patient care.", quote: "helps clinics save time and improve patient care"));

        var overview = await Service().GetAsync(_a);
        var main = overview.Questions.Single(q => q.Key == "main-benefit");
        await Service().RedraftAsync(_a, main.Id);

        Assert.Equal(AiTaskKinds.SuggestedAnswerGeneration, Assert.Single(_ai.Calls).Task);
        Assert.Equal("High", (await Service().GetAsync(_a)).Questions.Single(q => q.Key == "main-benefit").Answer!.Confidence);
    }

    // ----- what the application catches ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_quotation_that_is_not_in_the_source_is_dropped_and_an_answer_left_with_no_evidence_is_not_shown_as_an_answer()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 500 and includes free medicines.", quote: "the fee includes free medicines for every patient"));

        var answer = (await Ask()).Answer!;

        Assert.Equal("NoEvidence", answer.Confidence);
        Assert.Null(answer.Summary);
        Assert.Empty(answer.Evidence);
        Assert.Contains("none of its quotations could be found in your sources", answer.Limitation);
        Assert.False(string.IsNullOrWhiteSpace(answer.ClarifyingQuestion));
    }

    [Fact]
    public async Task A_figure_no_cited_passage_states_is_flagged_and_caps_the_confidence_at_low()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 999 for the first visit.", quote: "consultation fee is Rs 500 for the first visit"));

        var answer = (await Ask()).Answer!;

        Assert.Equal("Low", answer.Confidence);
        Assert.True(answer.HasUnsupportedClaim);
        Assert.Contains("999", answer.Limitation);
        Assert.Equal("The consultation fee is Rs 999 for the first visit.", answer.Summary); // shown, but flagged - the tenant decides
    }

    [Fact]
    public async Task A_model_that_says_the_sources_are_not_enough_gets_no_answer_and_its_clarifying_question_is_kept()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply(null, insufficient: true, clarify: "Is the fee different for follow-up visits?"));

        var answer = (await Ask()).Answer!;

        Assert.Equal("NoEvidence", answer.Confidence);
        Assert.Equal("Is the fee different for follow-up visits?", answer.ClarifyingQuestion);
        Assert.Null(answer.Summary);
    }

    [Fact]
    public async Task A_citation_of_a_passage_that_was_never_offered_is_dropped()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 500.", passage: "P9", quote: "consultation fee is Rs 500"));

        Assert.Equal("NoEvidence", (await Ask()).Answer!.Confidence);
    }

    [Fact]
    public async Task Sources_that_disagree_make_the_answer_low_and_keep_both_values_and_where_each_came_from()
    {
        await Source("brochure.pdf", _a, "The Premium Plan costs Rs 999 per month for every clinic.");
        await Source("clinic.example", _a, "The Premium Plan costs Rs 1,499 per month for every clinic.");
        // The two passages score the same, so which one is [P1] is not fixed: cite the label of the one that says 999.
        _ai.Respond = r => ScriptedAi.Live(Reply("The Premium Plan costs Rs 999 per month.", passage: System.Text.RegularExpressions.Regex.Match(r.User, @"\[(P\d+)\][^\n]*\n[^\n]*Rs 999").Groups[1].Value, quote: "Premium Plan costs Rs 999 per month"));

        var answer = (await Ask("How much does the Premium Plan cost per month?")).Answer!;

        Assert.Equal("Low", answer.Confidence);
        Assert.True(answer.HasConflict);
        var conflict = Assert.Single(answer.Conflicts);
        Assert.Equal("price", conflict.Fact);
        Assert.Equal(new[] { "999", "1499" }, conflict.Values.Select(v => v.Value).OrderBy(v => v.Length).ThenBy(v => v));
        Assert.Equal("Open", conflict.Status);
        Assert.Contains("disagree", answer.Limitation);
    }

    // ----- when there is nothing to go on ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_no_passage_bears_on_the_question_the_ai_is_not_asked_and_nothing_is_invented()
    {
        await Source("brochure.pdf", _a, "Parking is free for patients.");

        var answer = (await Ask("Do you offer international shipping?")).Answer!;

        Assert.Empty(_ai.Calls);
        Assert.Equal(("NoEvidence", "None"), (answer.Confidence, answer.Provider));
        Assert.Equal("None of your sources mention this.", answer.Limitation);
        Assert.False(string.IsNullOrWhiteSpace(answer.ClarifyingQuestion));
    }

    [Fact]
    public async Task With_no_sources_read_yet_the_limitation_says_so()
    {
        var answer = (await Ask()).Answer!;

        Assert.Contains("none of your sources has been read", answer.Limitation);
        Assert.Empty(_ai.Calls);
    }

    [Fact]
    public async Task The_simulators_output_is_never_an_answer_or_evidence()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Simulated("""{"simulated":true,"answer":"[SIMULATED] A predefined answer used for testing.","evidence":[],"insufficientEvidence":false}""");

        var answer = (await Ask()).Answer!;

        Assert.Equal("NoEvidence", answer.Confidence);
        Assert.True(answer.IsSimulated);
        Assert.Equal("Simulator", answer.Provider);
        Assert.Null(answer.Summary);
        Assert.Contains("Simulated test data", answer.Limitation);
        Assert.Empty(answer.Evidence);
    }

    [Fact]
    public async Task When_the_ai_fails_a_question_is_not_left_behind_and_the_reason_is_given()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Failure("rate limited");

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Ask());

        Assert.Contains("rate limited", ex.Message);
        Assert.Empty((await Service().GetAsync(_a)).Questions.Where(q => !q.IsStandard));
    }

    // ----- versions and approval ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_suggestion_is_a_new_version_and_the_earlier_one_is_kept_but_no_longer_current()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 500.", quote: "consultation fee is Rs 500"));
        var question = await Ask();

        var again = await Service().RedraftAsync(_a, question.Id);

        Assert.Equal(2, again.Answer!.Version);
        var all = await _db.KnowledgeAnswers.Where(a => a.QuestionId == question.Id).OrderBy(a => a.Version).ToListAsync();
        Assert.Equal(new[] { false, true }, all.Select(a => a.IsCurrent));
    }

    [Fact]
    public async Task An_answer_the_tenant_approved_is_never_replaced_by_a_new_draft()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 500.", quote: "consultation fee is Rs 500"));
        var question = await Ask();
        var approved = await _db.KnowledgeAnswers.SingleAsync(a => a.QuestionId == question.Id);
        approved.Status = KnowledgeVerificationStatus.VerifiedAndApproved;
        await _db.SaveChangesAsync();
        _ai.Calls.Clear();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().RedraftAsync(_a, question.Id));

        Assert.Contains("already approved", ex.Message);
        Assert.Empty(_ai.Calls);
        Assert.Equal(1, await _db.KnowledgeAnswers.CountAsync(a => a.QuestionId == question.Id));
    }

    // ----- the questions -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_tenant_gets_the_twelve_standard_questions_once()
    {
        var first = await Service().GetAsync(_a);
        var second = await Service().GetAsync(_a);

        Assert.Equal(12, first.Questions.Count);
        Assert.Equal(12, second.Questions.Count);
        Assert.All(first.Questions, q => Assert.True(q.IsStandard));
        Assert.All(first.Questions, q => Assert.Null(q.Answer)); // nothing is asked of the tenant, and nothing is drafted until they say so
    }

    [Theory]
    [InlineData("Too short", "text")]
    [InlineData("", "text")]
    public async Task A_question_must_be_a_real_sentence_of_reasonable_length(string text, string property)
    {
        if (text == "Too short")
            text = "Why?";
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Ask(text));
        Assert.Contains(ex.Errors, e => e.PropertyName == property);

        await Assert.ThrowsAsync<ValidationException>(() => Ask(new string('x', 501)));
    }

    [Fact]
    public async Task The_number_of_own_questions_is_limited_and_a_repeat_is_refused()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 500.", quote: "consultation fee is Rs 500"));
        await Ask("What is the consultation fee at the clinic?");

        await Assert.ThrowsAsync<ConflictException>(() => Ask("What is the consultation fee at the clinic?"));
        await Ask("Is the consultation fee refundable at the clinic?");
        await Ask("Does the consultation fee include a follow-up visit?");

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Ask("Can the consultation fee be paid online at the clinic?"));
        Assert.Contains("the most allowed", ex.Message);
    }

    [Fact]
    public async Task Own_questions_can_be_removed_but_the_standard_ones_cannot()
    {
        var question = await Ask();
        await Service().DeleteQuestionAsync(_a, question.Id);

        var overview = await Service().GetAsync(_a);
        Assert.DoesNotContain(overview.Questions, q => q.Id == question.Id);

        var standard = overview.Questions.First(q => q.IsStandard);
        Assert.Contains("cannot be removed", (await Assert.ThrowsAsync<ConflictException>(() => Service().DeleteQuestionAsync(_a, standard.Id))).Message);
    }

    // ----- one tenant never sees another's -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_sources_are_never_offered_to_the_ai_and_their_questions_are_not_found()
    {
        await Source("theirs.pdf", _b, "The consultation fee is Rs 7777 at their secret clinic.");
        await Source("mine.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 500.", quote: "consultation fee is Rs 500"));

        await Ask();

        Assert.DoesNotContain("7777", Assert.Single(_ai.Calls).User);
        Assert.DoesNotContain("theirs.pdf", _ai.Calls[0].User);

        // Tenant B's own question, created through B's scope, is invisible to A.
        _tenant.TenantId = _b;
        var theirQuestion = await Service().AskAsync(_b, null, new AskQuestionRequest("What do you charge for a consultation visit?"));
        _tenant.TenantId = _a;
        await Assert.ThrowsAsync<NotFoundException>(() => Service().RedraftAsync(_a, theirQuestion.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteQuestionAsync(_a, theirQuestion.Id));
        Assert.DoesNotContain((await Service().GetAsync(_a)).Questions, q => q.Id == theirQuestion.Id);
    }

    // ----- the platform's switch ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_suggested_answers_switched_off_nothing_is_drafted_and_the_message_says_why()
    {
        _routing.Suggested = new AiFeatureChoice(false, "OpenAI", IsOff: true);
        await Source("brochure.pdf", _a, FeePassage);

        Assert.Contains("switched off", (await Assert.ThrowsAsync<FeatureDisabledException>(() => Ask())).Message);
        await Assert.ThrowsAsync<FeatureDisabledException>(() => Service().StartRunAsync(_a, null));
        Assert.Equal("Off", (await Service().GetAsync(_a)).AiMode);
        Assert.Empty(_ai.Calls);
    }

    [Fact]
    public async Task The_overview_says_what_drafting_runs_on_and_how_much_there_is_to_draft_from()
    {
        await Source("brochure.pdf", _a, FeePassage, "Parking is free.");
        var onApi = await Service().GetAsync(_a);
        Assert.Equal(("Api", "OpenAI", 1, 2), (onApi.AiMode, onApi.AiProvider, onApi.ReadySources, onApi.Passages));

        _routing.Suggested = new AiFeatureChoice(false, "OpenAI");
        var sim = await Service().GetAsync(_a);
        Assert.Equal(("Simulator", "Simulator"), (sim.AiMode, sim.AiProvider));
    }

    // ----- the run ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_needs_a_source_that_has_been_read_and_a_second_start_returns_the_run_already_going()
    {
        Assert.Contains("nothing to draft answers from", (await Assert.ThrowsAsync<ConflictException>(() => Service().StartRunAsync(_a, null))).Message);

        await Source("brochure.pdf", _a, FeePassage);
        var first = await Service().StartRunAsync(_a, null);
        var second = await Service().StartRunAsync(_a, null);

        Assert.Equal("Queued", first.Status);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(_queue.Queued);
    }

    [Fact]
    public async Task A_run_drafts_every_question_then_the_features_and_reports_what_it_did()
    {
        await Source("brochure.pdf", _a,
            FeePassage,
            "The software helps clinics save time and improve patient care, a key advantage for busy doctors.",
            "Features include digital patient records, appointment reminders and online billing for the clinic.");
        _ai.Respond = r => r.Task switch
        {
            AiTaskKinds.BenefitAnalysis => ScriptedAi.Live(JsonSerializer.Serialize(new
            {
                features = new object[]
                {
                    new { feature = "Digital patient records", problem = "Paper files are slow to search", benefit = "Staff find records faster", segment = "Clinics", inference = true,
                        evidence = new[] { new { passage = "P1", quote = "digital patient records, appointment reminders and online billing" } } },
                    new { feature = "AI diagnosis", problem = (string?)null, benefit = "Reduces errors by 40%", segment = (string?)null, inference = false,
                        evidence = new[] { new { passage = "P1", quote = "this feature is not in the passage at all" } } },
                }
            })),
            _ => ScriptedAi.Live(Reply("It helps clinics save time and improve patient care.", quote: "helps clinics save time and improve patient care")),
        };
        var run = await Service().StartRunAsync(_a, null);

        await Service().RunAsync(run.Id);

        var done = await Service().GetRunAsync(_a, run.Id);
        Assert.Equal("Completed", done.Status);
        Assert.Equal(13, done.Total);
        Assert.Equal(13, done.Done);
        Assert.Equal(12, done.Drafted + done.NoEvidence + done.Skipped + done.Failed);
        Assert.True(done.Drafted >= 1);
        Assert.NotNull(done.FinishedAt);

        var overview = await Service().GetAsync(_a);
        var feature = Assert.Single(overview.Features); // the unsupported feature is left out, not guessed
        Assert.Equal("Digital patient records", feature.Feature);
        Assert.Equal("brochure.pdf", Assert.Single(feature.Evidence).SourceName);
        Assert.Equal(DraftRunStatus(overview), "Completed");
    }

    private static string DraftRunStatus(KnowledgeAnswersOverviewDto overview) => overview.Run!.Status;

    [Fact]
    public async Task A_benefit_with_a_number_no_source_gives_is_flagged_not_accepted()
    {
        await Source("brochure.pdf", _a, "Features include digital patient records and online billing for the clinic.");
        _ai.Respond = r => r.Task == AiTaskKinds.BenefitAnalysis
            ? ScriptedAi.Live(JsonSerializer.Serialize(new
            {
                features = new[]
                {
                    new { feature = "Online billing", problem = (string?)null, benefit = "Cuts billing time by 60%", segment = (string?)null, inference = false,
                        evidence = new[] { new { passage = "P1", quote = "digital patient records and online billing for the clinic" } } },
                }
            }))
            : ScriptedAi.Live(Reply(null, insufficient: true));
        var run = await Service().StartRunAsync(_a, null);

        await Service().RunAsync(run.Id);

        var feature = Assert.Single((await Service().GetAsync(_a)).Features);
        Assert.True(feature.HasUnsupportedClaim);
        Assert.Equal("Low", feature.Confidence);
    }

    [Fact]
    public async Task A_simulated_or_failed_feature_call_leaves_the_existing_features_alone()
    {
        await Source("brochure.pdf", _a, "Features include digital patient records and online billing for the clinic.");
        _db.FeatureBenefits.Add(new FeatureBenefit { TenantId = _a, Feature = "Existing", Provider = "OpenAI", Confidence = KnowledgeConfidence.High, GeneratedAt = DateTime.UtcNow, RunVersion = 1 });
        await _db.SaveChangesAsync();
        _ai.Respond = r => r.Task == AiTaskKinds.BenefitAnalysis ? ScriptedAi.Simulated("""{"simulated":true}""") : ScriptedAi.Failure("down");
        var run = await Service().StartRunAsync(_a, null);

        await Service().RunAsync(run.Id);

        Assert.Equal("Existing", Assert.Single((await Service().GetAsync(_a)).Features).Feature);
        var done = await Service().GetRunAsync(_a, run.Id);
        Assert.Equal("Completed", done.Status);
        Assert.True(done.Failed > 0);
        Assert.Contains("down", done.Error);
    }

    [Fact]
    public async Task A_run_started_just_before_the_switch_went_off_does_not_draft()
    {
        await Source("brochure.pdf", _a, FeePassage);
        var run = await Service().StartRunAsync(_a, null);
        _routing.Suggested = new AiFeatureChoice(false, "OpenAI", IsOff: true);

        await Service().RunAsync(run.Id);

        var done = await Service().GetRunAsync(_a, run.Id);
        Assert.Equal("Failed", done.Status);
        Assert.Contains("switched off", done.Error);
        Assert.Empty(_ai.Calls);
    }

    [Fact]
    public async Task Approved_answers_are_skipped_by_a_run_and_another_tenants_run_is_not_found()
    {
        await Source("brochure.pdf", _a, FeePassage);
        _ai.Respond = _ => ScriptedAi.Live(Reply("The consultation fee is Rs 500.", quote: "consultation fee is Rs 500"));
        var question = await Ask();
        var approved = await _db.KnowledgeAnswers.SingleAsync(a => a.QuestionId == question.Id);
        approved.Status = KnowledgeVerificationStatus.VerifiedAndApproved;
        await _db.SaveChangesAsync();
        var run = await Service().StartRunAsync(_a, null);

        await Service().RunAsync(run.Id);

        Assert.Equal(1, (await Service().GetRunAsync(_a, run.Id)).Skipped);
        _tenant.TenantId = _b;
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetRunAsync(_b, run.Id));
    }

    [Fact]
    public void The_endpoints_are_tenant_admin_only_and_the_ai_gets_no_tenant_secrets()
    {
        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ProductKnowledgeAnswersController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Domain.Constants.AppRoles.Admin, authorize.Roles);
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));

        // The prompt only ever carries the question and numbered passages - no tenant id, no source id, no key.
        var text = DraftPrompts.AnswerUser("Q?", new[] { new PassageCandidate(Guid.NewGuid(), Guid.NewGuid(), "brochure.pdf", 1, "Page 1", null, "Text.", "h") });
        Assert.DoesNotMatch(@"[0-9a-f]{8}-[0-9a-f]{4}", text);
    }
}
