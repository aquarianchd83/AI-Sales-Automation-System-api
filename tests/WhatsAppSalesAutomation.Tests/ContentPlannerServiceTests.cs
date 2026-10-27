using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Application.ContentStudio.Planner;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The content planner: topic ideas and scripts are written only from verified facts, charged to the tenant's studio quota and returned when
/// nothing usable comes back; every scene is checked in code against the facts; a script is a draft that can be edited; nothing crosses tenants.
/// </summary>
public sealed class ContentPlannerServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

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

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => Now;
        public DateTime IstNow => Now.AddHours(5.5);
    }

    /// <summary>Reads the verified facts from the database the way the real retrieval does, numbered in the prompt block.</summary>
    private sealed class FactsFromDb : IKnowledgeRetrievalService
    {
        private readonly IApplicationDbContext _db;
        public FactsFromDb(IApplicationDbContext db) => _db = db;

        public async Task<GroundingResultDto> RetrieveAsync(Guid tenantId, string? query, int limit = 12, CancellationToken cancellationToken = default)
        {
            var facts = await _db.BusinessFacts.IgnoreQueryFilters().AsNoTracking()
                .Where(f => f.TenantId == tenantId && f.Status == BusinessFactStatus.Verified).OrderBy(f => f.CreatedAt).Take(limit).ToListAsync(cancellationToken);
            var dtos = facts.Select(f => new GroundingFactDto(f.Id, f.Category, f.Subject, f.Statement, f.Context, 1)).ToList();
            var block = new StringBuilder("Verified facts:\n");
            for (var i = 0; i < dtos.Count; i++)
                block.AppendLine($"[{i + 1}] {dtos[i].Statement}");
            return new GroundingResultDto(dtos, block.ToString());
        }
    }

    private sealed class FakeMetering : IStudioMeteringService
    {
        public bool Deny { get; set; }
        public List<StudioChargeRequest> Charges { get; } = new();
        public List<Guid> Completed { get; } = new();
        public List<(Guid Id, string Reason)> Refunded { get; } = new();

        public Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default)
        {
            if (Deny)
                return Task.FromResult(new StudioChargeResult(false, Guid.Empty, false, "Your content generations are used up for this period."));
            Charges.Add(request);
            return Task.FromResult(new StudioChargeResult(true, Guid.NewGuid(), false, null));
        }

        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default)
        {
            Completed.Add(activityId);
            return Task.CompletedTask;
        }

        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default)
        {
            Refunded.Add((activityId, reason));
            return Task.CompletedTask;
        }

        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
    private readonly Admin _admin = new();
    private readonly FakeMetering _metering = new();
    private readonly ScriptedAi _ai = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public ContentPlannerServiceTests()
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

    private ContentPlannerService Service() => new(_db, _admin, new Clock(), new FactsFromDb(_db), _metering, _ai);

    private async Task<BusinessFact> Fact(string statement, Guid? tenant = null, BusinessFactStatus status = BusinessFactStatus.Verified, string? evidence = null)
    {
        var fact = new BusinessFact
        {
            TenantId = tenant ?? _a, Category = "Services", Statement = statement, Status = status, Evidence = evidence,
            VerifiedAtUtc = status == BusinessFactStatus.Verified ? Now : null, VerifiedBy = status == BusinessFactStatus.Verified ? _admin.UserId : null
        };
        _db.BusinessFacts.Add(fact);
        await _db.SaveChangesAsync();
        return fact;
    }

    private const string OpenHours = "The clinic is open Monday to Saturday from 9 am to 6 pm.";
    private const string Cataract = "The clinic performs cataract surgery with foldable lenses.";

    private async Task<ProjectDetailDto> NewProject(Guid? tenant = null)
    {
        _tenant.TenantId = tenant ?? _a;
        var project = await Service().CreateAsync(tenant ?? _a, new CreateProjectRequest("Cataract awareness", "Get more bookings for cataract surgery", "English"));
        _tenant.TenantId = _a;
        return project;
    }

    private static string Topics(params (string Title, int[] Facts)[] topics) =>
        JsonSerializer.Serialize(new { topics = topics.Select(t => new { title = t.Title, rationale = "It suits the goal.", facts = t.Facts }) });

    private static string Scenes(params (string Narration, int[] Facts)[] scenes) =>
        JsonSerializer.Serialize(new { scenes = scenes.Select(s => new { narration = s.Narration, onScreenText = "Hello", durationSeconds = 8, visualHint = "The clinic", facts = s.Facts }) });

    // ----- projects --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_project_needs_a_title_and_a_goal_and_starts_as_a_draft_with_the_number_of_facts_the_writer_has()
    {
        await Fact(OpenHours);
        await Fact("An unverified claim about prices.", status: BusinessFactStatus.Draft);
        await Assert.ThrowsAsync<ValidationException>(() => Service().CreateAsync(_a, new CreateProjectRequest(" ", "A goal", null)));
        await Assert.ThrowsAsync<ValidationException>(() => Service().CreateAsync(_a, new CreateProjectRequest("A title", "", null)));

        var project = await Service().CreateAsync(_a, new CreateProjectRequest("  Cataract awareness ", "Get more bookings", null));

        Assert.Equal(("Cataract awareness", "English", "Draft", 1), (project.Title, project.Language, project.Status, project.UsableFacts));
        Assert.Empty(project.Topics);
        Assert.Null(project.CurrentVersion);
        var list = await Service().ListAsync(_a);
        Assert.Equal(project.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task A_project_can_be_renamed_and_a_deleted_one_is_gone_from_the_list_and_not_found()
    {
        var project = await NewProject();

        var updated = await Service().UpdateAsync(_a, project.Id, new UpdateProjectRequest("Renamed", null, "Hindi"));
        Assert.Equal(("Renamed", "Hindi", project.Goal), (updated.Title, updated.Language, updated.Goal));
        await Assert.ThrowsAsync<ValidationException>(() => Service().UpdateAsync(_a, project.Id, new UpdateProjectRequest("", null, null)));

        await Service().DeleteAsync(_a, project.Id);

        Assert.Empty(await Service().ListAsync(_a));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetAsync(_a, project.Id));
    }

    // ----- topics ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Topics_are_written_from_the_verified_facts_charged_once_and_keep_which_facts_they_rest_on()
    {
        var hours = await Fact(OpenHours);
        var cataract = await Fact(Cataract);
        var project = await NewProject();
        _ai.Respond = _ => ScriptedAi.Live(Topics(("Why cataract surgery is quick", new[] { 2 }), ("When we are open", new[] { 1, 2, 9 })));

        var detail = await Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(4));

        Assert.Equal(new[] { "Why cataract surgery is quick", "When we are open" }, detail.Topics.Select(t => t.Title));
        Assert.Equal(new[] { 1, 2 }, detail.Topics.Select(t => t.FactCount)); // the invented fact number 9 is dropped
        Assert.All(detail.Topics, t => Assert.Equal(("Suggested", false, "OpenAI"), (t.Status, t.IsSimulated, t.Provider)));
        var stored = (await _db.ContentTopics.ToListAsync()).Single(t => t.Title == "Why cataract surgery is quick");
        Assert.Equal(new[] { cataract.Id }, JsonSerializer.Deserialize<List<Guid>>(stored.FactIdsJson));
        Assert.Contains(hours.Id, JsonSerializer.Deserialize<List<Guid>>((await _db.ContentTopics.SingleAsync(t => t.Title == "When we are open")).FactIdsJson)!);

        var call = Assert.Single(_ai.Calls);
        Assert.Equal(AiTaskKinds.ContentTopicIdeas, call.Task);
        Assert.Contains(OpenHours, call.User); // the writer is given the verified facts...
        Assert.Contains("Get more bookings for cataract surgery", call.User);
        var charge = Assert.Single(_metering.Charges);
        Assert.Equal((_a, StudioActivityKind.TopicIdeas), (charge.TenantId, charge.Kind));
        Assert.Single(_metering.Completed);
        Assert.Empty(_metering.Refunded);
    }

    [Fact]
    public async Task Nothing_is_asked_or_charged_when_the_tenant_has_no_verified_facts()
    {
        await Fact("Only a draft.", status: BusinessFactStatus.Draft);
        var project = await NewProject();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null)));

        Assert.Contains("no verified business facts", ex.Message);
        Assert.Empty(_ai.Calls);
        Assert.Empty(_metering.Charges);
    }

    [Fact]
    public async Task A_denied_charge_stops_the_work_before_the_ai_is_called()
    {
        await Fact(OpenHours);
        var project = await NewProject();
        _metering.Deny = true;

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null)));

        Assert.Contains("used up", ex.Message);
        Assert.Empty(_ai.Calls);
    }

    [Fact]
    public async Task A_failed_call_or_a_reply_with_nothing_usable_returns_the_charge_and_saves_nothing()
    {
        await Fact(OpenHours);
        var project = await NewProject();

        _ai.Respond = _ => ScriptedAi.Failure("The provider is down.");
        var failed = await Assert.ThrowsAsync<ConflictException>(() => Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null)));
        Assert.Contains("The provider is down.", failed.Message);
        Assert.Contains("Nothing was charged", failed.Message);

        // An idea that names no fact is a guess: dropped, and the reply counts as unusable.
        _ai.Respond = _ => ScriptedAi.Live(Topics(("A guess", Array.Empty<int>())));
        var guess = await Assert.ThrowsAsync<ConflictException>(() => Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null)));
        Assert.Contains("rests on your verified facts", guess.Message);

        _ai.Respond = _ => ScriptedAi.Live("not json at all");
        await Assert.ThrowsAsync<ConflictException>(() => Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null)));

        Assert.Equal(3, _metering.Refunded.Count);
        Assert.Empty(_metering.Completed);
        Assert.Empty(await _db.ContentTopics.ToListAsync());
    }

    [Fact]
    public async Task Work_done_by_the_platforms_test_mode_is_kept_but_marked_as_test_data_and_costs_nothing()
    {
        await Fact(OpenHours);
        var project = await NewProject();
        _ai.Respond = _ => ScriptedAi.Simulated(Topics(("[SIMULATED] A predefined idea", Array.Empty<int>())));

        var detail = await Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null));

        var topic = Assert.Single(detail.Topics);
        Assert.Equal((true, 0, "Simulator"), (topic.IsSimulated, topic.FactCount, topic.Provider));
        Assert.Contains("test mode", Assert.Single(_metering.Refunded).Reason);
        Assert.Empty(_metering.Completed);
    }

    [Fact]
    public async Task Choosing_a_topic_unchooses_the_previous_one_and_a_rejected_topic_is_not_written_about()
    {
        await Fact(OpenHours);
        var project = await NewProject();
        _ai.Respond = _ => ScriptedAi.Live(Topics(("One", new[] { 1 }), ("Two", new[] { 1 })));
        var detail = await Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null));
        var one = detail.Topics.Single(t => t.Title == "One");
        var two = detail.Topics.Single(t => t.Title == "Two");

        await Service().UpdateTopicAsync(_a, one.Id, new UpdateTopicRequest(null, "Chosen"));
        var after = await Service().UpdateTopicAsync(_a, two.Id, new UpdateTopicRequest("Two, reworded", "Chosen"));

        Assert.Equal(new[] { ("One", "Suggested"), ("Two, reworded", "Chosen") }, after.Topics.Select(t => (t.Title, t.Status)));
        await Assert.ThrowsAsync<ValidationException>(() => Service().UpdateTopicAsync(_a, one.Id, new UpdateTopicRequest(null, "Maybe")));

        await Service().UpdateTopicAsync(_a, one.Id, new UpdateTopicRequest(null, "Rejected"));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GenerateScriptAsync(_a, project.Id, new GenerateScriptRequest(one.Id, null, null)));
    }

    // ----- scripts ---------------------------------------------------------------------------------------------------------------

    private async Task<(ProjectDetailDto Project, TopicDto Topic)> ProjectWithChosenTopic()
    {
        await Fact(OpenHours);
        await Fact(Cataract);
        var project = await NewProject();
        _ai.Respond = _ => ScriptedAi.Live(Topics(("Why cataract surgery is quick", new[] { 2 })));
        var withTopic = await Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null));
        var topic = Assert.Single(withTopic.Topics);
        await Service().UpdateTopicAsync(_a, topic.Id, new UpdateTopicRequest(null, "Chosen"));
        _metering.Charges.Clear();
        _metering.Completed.Clear();
        _ai.Calls.Clear();
        return (project, topic);
    }

    [Fact]
    public async Task A_script_has_scenes_and_the_application_not_the_ai_decides_which_ones_have_a_fact_behind_them()
    {
        var (project, topic) = await ProjectWithChosenTopic();
        _ai.Respond = _ => ScriptedAi.Live(Scenes(
            ("We are open Monday to Saturday from 9 am to 6 pm.", new[] { 1 }),          // backed by a verified fact
            ("We have treated more than 10,000 patients.", new[] { 1 }),                  // the AI claims fact 1 backs it; it does not
            ("Book your visit today.", Array.Empty<int>())));                            // an invitation states nothing

        var detail = await Service().GenerateScriptAsync(_a, project.Id, new GenerateScriptRequest(topic.Id, 40, "Keep it friendly"));

        var version = detail.CurrentVersion!;
        Assert.Equal((1, "Draft", 40, topic.Title, "OpenAI", PlannerPrompts.Version, false), (version.Number, version.Status, version.TargetSeconds, version.TopicTitle, version.Provider, version.PromptVersion, version.IsSimulated));
        Assert.Equal(new[] { "Supported", "Unsupported", "NoClaim" }, version.Scenes.Select(s => s.ClaimStatus));
        Assert.Equal((2, 1), (version.Supported, version.Flagged));
        Assert.Contains("10,000", version.Scenes[1].ClaimNote);
        Assert.Equal(new[] { 1, 2, 3 }, version.Scenes.Select(s => s.Order));
        Assert.Equal(24, version.TotalSeconds);
        Assert.Equal(OpenHours, Assert.Single(version.Scenes[0].Facts).Statement); // cited facts are shown with their wording
        Assert.Contains("Book your visit today.", (await _db.ContentVersions.SingleAsync()).ScriptText);
        Assert.Equal(64, (await _db.ContentVersions.SingleAsync()).ContentHash.Length);
        Assert.Equal(("Scripted", version.Id), (detail.Status, detail.CurrentVersion!.Id));

        var call = Assert.Single(_ai.Calls);
        Assert.Equal(AiTaskKinds.ContentScript, call.Task);
        Assert.Contains("Keep it friendly", call.User);
        var charge = Assert.Single(_metering.Charges);
        Assert.Equal(StudioActivityKind.Script, charge.Kind);
        Assert.Equal(_metering.Completed.Single(), version.ActivityId);
    }

    [Fact]
    public async Task A_script_needs_a_chosen_topic_and_each_new_script_is_a_new_version_the_old_ones_are_kept()
    {
        var (project, topic) = await ProjectWithChosenTopic();
        _ai.Respond = _ => ScriptedAi.Live(Scenes(("We are open Monday to Saturday from 9 am to 6 pm.", new[] { 1 })));

        var first = await Service().GenerateScriptAsync(_a, project.Id, new GenerateScriptRequest(null, null, null)); // the chosen topic is used
        var second = await Service().GenerateScriptAsync(_a, project.Id, new GenerateScriptRequest(topic.Id, 90, null));

        Assert.Equal(1, first.CurrentVersion!.Number);
        Assert.Equal(2, second.CurrentVersion!.Number);
        Assert.Equal(new[] { 2, 1 }, second.Versions.Select(v => v.Number));
        Assert.Equal(DefaultSeconds, first.CurrentVersion.TargetSeconds);
        Assert.Equal(90, second.CurrentVersion.TargetSeconds);
        Assert.Equal(2, await _db.ContentVersions.CountAsync());
        Assert.Equal(first.CurrentVersion.Scenes[0].Narration, (await Service().GetVersionAsync(_a, first.CurrentVersion.Id)).Scenes[0].Narration);

        // Un-choose and there is no topic to write about.
        await Service().UpdateTopicAsync(_a, topic.Id, new UpdateTopicRequest(null, "Suggested"));
        await Assert.ThrowsAsync<ValidationException>(() => Service().GenerateScriptAsync(_a, project.Id, new GenerateScriptRequest(null, null, null)));
    }

    private const int DefaultSeconds = ContentPlannerService.DefaultTargetSeconds;

    [Fact]
    public async Task A_reply_that_cannot_be_read_as_a_script_returns_the_charge_and_writes_nothing()
    {
        var (project, topic) = await ProjectWithChosenTopic();
        _ai.Respond = _ => ScriptedAi.Live("{\"scenes\":\"none\"}");

        await Assert.ThrowsAsync<ConflictException>(() => Service().GenerateScriptAsync(_a, project.Id, new GenerateScriptRequest(topic.Id, null, null)));

        Assert.Single(_metering.Refunded);
        Assert.Empty(_metering.Completed);
        Assert.Empty(await _db.ContentVersions.ToListAsync());
        Assert.Empty(await _db.ContentScenes.ToListAsync());
    }

    [Fact]
    public async Task A_test_mode_script_is_marked_as_test_data()
    {
        var (project, topic) = await ProjectWithChosenTopic();
        _ai.Respond = _ => ScriptedAi.Simulated(Scenes(("[SIMULATED] Welcome. This is a predefined test script.", Array.Empty<int>())));

        var version = (await Service().GenerateScriptAsync(_a, project.Id, new GenerateScriptRequest(topic.Id, null, null))).CurrentVersion!;

        Assert.Equal((true, "Simulator"), (version.IsSimulated, version.Provider));
        Assert.Empty(_metering.Completed);
        Assert.Single(_metering.Refunded);
    }

    // ----- editing ---------------------------------------------------------------------------------------------------------------

    private async Task<ScriptVersionDto> DraftScript()
    {
        var (project, topic) = await ProjectWithChosenTopic();
        _ai.Respond = _ => ScriptedAi.Live(Scenes(
            ("We are open Monday to Saturday from 9 am to 6 pm.", new[] { 1 }),
            ("We perform cataract surgery with foldable lenses.", new[] { 2 }),
            ("Book your visit today.", Array.Empty<int>())));
        return (await Service().GenerateScriptAsync(_a, project.Id, new GenerateScriptRequest(topic.Id, null, null))).CurrentVersion!;
    }

    [Fact]
    public async Task Editing_a_scene_checks_it_again_marks_it_as_the_tenants_own_and_rebuilds_the_script_and_its_fingerprint()
    {
        var version = await DraftScript();
        var before = (await _db.ContentVersions.SingleAsync()).ContentHash;
        var scene = version.Scenes[0];

        var edited = await Service().UpdateSceneAsync(_a, scene.Id, new UpdateSceneRequest("We are open twenty four hours every day.", "Open Sunday", 500, "  Reception "));

        var changed = edited.Scenes[0];
        Assert.Equal(("Unsupported", true, PlannerReply.MaxSceneSeconds, "Reception"), (changed.ClaimStatus, changed.EditedByTenant, changed.DurationSeconds, changed.VisualHint));
        Assert.Equal((1, 2), (edited.Flagged, edited.Supported)); // the other two scenes are untouched
        var stored = await _db.ContentVersions.SingleAsync();
        Assert.StartsWith("We are open twenty four hours", stored.ScriptText);
        Assert.NotEqual(before, stored.ContentHash);
        await Assert.ThrowsAsync<ValidationException>(() => Service().UpdateSceneAsync(_a, scene.Id, new UpdateSceneRequest(" ", null, 8, null)));

        // Putting the supported wording back supports it again.
        var fixedAgain = await Service().UpdateSceneAsync(_a, scene.Id, new UpdateSceneRequest("We are open Monday to Saturday from 9 am to 6 pm.", "Hello", 8, "The clinic"));
        Assert.Equal("Supported", fixedAgain.Scenes[0].ClaimStatus);
        Assert.Equal(before, (await _db.ContentVersions.SingleAsync()).ContentHash); // same words, same fingerprint
    }

    [Fact]
    public async Task Deleting_a_scene_renumbers_the_rest_and_the_last_scene_cannot_be_removed()
    {
        var version = await DraftScript();

        var after = await Service().DeleteSceneAsync(_a, version.Scenes[0].Id);
        Assert.Equal(new[] { 1, 2 }, after.Scenes.Select(s => s.Order));
        Assert.StartsWith("We perform cataract surgery", (await _db.ContentVersions.SingleAsync()).ScriptText);

        await Service().DeleteSceneAsync(_a, after.Scenes[0].Id);
        var lastId = (await _db.ContentScenes.SingleAsync()).Id;
        await Assert.ThrowsAsync<ValidationException>(() => Service().DeleteSceneAsync(_a, lastId));
        Assert.Single(await _db.ContentScenes.ToListAsync());
    }

    [Fact]
    public async Task A_script_that_is_no_longer_a_draft_cannot_be_edited()
    {
        var version = await DraftScript();
        var stored = await _db.ContentVersions.SingleAsync();
        stored.Status = ContentVersionStatus.InReview;
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().UpdateSceneAsync(_a, version.Scenes[0].Id, new UpdateSceneRequest("Changed.", null, 8, null)));
        Assert.Contains("no longer a draft", ex.Message);
        await Assert.ThrowsAsync<ConflictException>(() => Service().DeleteSceneAsync(_a, version.Scenes[0].Id));
        Assert.Equal("We are open Monday to Saturday from 9 am to 6 pm.", (await _db.ContentScenes.OrderBy(s => s.Order).FirstAsync()).Narration);
    }

    [Fact]
    public async Task Checking_again_follows_the_facts_as_they_are_now()
    {
        var version = await DraftScript();
        Assert.Equal((3, 0), (version.Supported, version.Flagged)); // two scenes backed, one invitation that needs no fact

        var hours = await _db.BusinessFacts.IgnoreQueryFilters().SingleAsync(f => f.Statement == OpenHours);
        hours.Status = BusinessFactStatus.Retired; // no longer true
        await _db.SaveChangesAsync();

        var rechecked = await Service().RecheckAsync(_a, version.Id);

        Assert.Equal("Unsupported", rechecked.Scenes[0].ClaimStatus);
        Assert.Equal(("Supported", "NoClaim"), (rechecked.Scenes[1].ClaimStatus, rechecked.Scenes[2].ClaimStatus));
        Assert.Equal(1, rechecked.Flagged);
    }

    // ----- isolation -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_projects_topics_scenes_and_versions_are_never_seen_or_changed()
    {
        var version = await DraftScript();
        var project = await _db.ContentProjects.SingleAsync();
        var topic = await _db.ContentTopics.SingleAsync();
        var scene = version.Scenes[0];

        Assert.Empty(await Service().ListAsync(_b));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetAsync(_b, project.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().UpdateAsync(_b, project.Id, new UpdateProjectRequest("Mine now", null, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteAsync(_b, project.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GenerateTopicsAsync(_b, project.Id, new GenerateTopicsRequest(null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().UpdateTopicAsync(_b, topic.Id, new UpdateTopicRequest("x", null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GenerateScriptAsync(_b, project.Id, new GenerateScriptRequest(topic.Id, null, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetVersionAsync(_b, version.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().RecheckAsync(_b, version.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().UpdateSceneAsync(_b, scene.Id, new UpdateSceneRequest("Hijacked.", null, 8, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DeleteSceneAsync(_b, scene.Id));

        Assert.Equal("Cataract awareness", (await _db.ContentProjects.SingleAsync()).Title);
        Assert.Equal(scene.Narration, (await _db.ContentScenes.OrderBy(s => s.Order).FirstAsync()).Narration);
    }

    [Fact]
    public async Task Another_tenants_facts_are_never_used_to_back_or_to_write_content()
    {
        await Fact("The clinic offers a free parking lot for patients.", tenant: _b);
        await Fact(OpenHours);
        var project = await NewProject();
        _ai.Respond = _ => ScriptedAi.Live(Topics(("Opening hours", new[] { 1 })));

        await Service().GenerateTopicsAsync(_a, project.Id, new GenerateTopicsRequest(null));

        Assert.DoesNotContain("parking", Assert.Single(_ai.Calls).User);
    }

    // ----- the pieces ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_reply_parser_keeps_only_what_is_usable_and_bounds_it()
    {
        Assert.Empty(PlannerReply.ParseTopics(null, 3, 5, true));
        Assert.Empty(PlannerReply.ParseTopics("[1,2]", 3, 5, true));
        Assert.Empty(PlannerReply.ParseScenes("{\"scenes\": {}}", 3));

        var topics = PlannerReply.ParseTopics(Topics(("A", new[] { 1, 1, 7 }), ("", new[] { 1 }), ("B", new[] { 2 }), ("C", new[] { 3 })), 3, 2, true);
        Assert.Equal(new[] { "A", "B" }, topics.Select(t => t.Title)); // blank title skipped, at most 2 kept
        Assert.Equal(new[] { 1 }, topics[0].FactNumbers);               // a repeated number once, a number that does not exist dropped

        var scenes = PlannerReply.ParseScenes("{\"scenes\":[{\"narration\":\"One.\",\"durationSeconds\":0},{\"narration\":\"Two.\",\"durationSeconds\":999},{\"narration\":\"\"},{\"x\":1}]}", 3);
        Assert.Equal(new[] { PlannerReply.MinSceneSeconds, PlannerReply.MaxSceneSeconds }, scenes.Select(s => s.DurationSeconds));
        Assert.Equal(PlannerReply.MaxScenes, PlannerReply.ParseScenes(Scenes(Enumerable.Range(0, 30).Select(i => ($"Scene {i}.", Array.Empty<int>())).ToArray()), 0).Count);
    }

    [Theory]
    [InlineData("Welcome to our clinic.", false)]
    [InlineData("Book your visit today.", false)]
    [InlineData("Are you tired of blurry vision?", false)]
    [InlineData("Hi", false)]
    [InlineData("We offer cataract surgery with foldable lenses.", true)]
    [InlineData("Surgery takes about fifteen minutes.", true)]
    public void Only_a_sentence_that_states_something_needs_a_fact(string sentence, bool statesSomething)
        => Assert.Equal(statesSomething, PlannerClaims.StatesSomething(sentence));

    [Fact]
    public void The_fingerprint_changes_only_when_the_words_change()
    {
        var a = PlannerClaims.Hash(new[] { ("One.", (string?)"x"), ("Two.", null) });
        Assert.Equal(a, PlannerClaims.Hash(new[] { ("One.", (string?)"x"), ("Two.", null) }));
        Assert.NotEqual(a, PlannerClaims.Hash(new[] { ("One.", (string?)"x"), ("Two!", null) }));
        Assert.NotEqual(a, PlannerClaims.Hash(new[] { ("Two.", (string?)null), ("One.", "x") }));
    }

    [Fact]
    public void The_endpoints_are_for_admins_and_managers_only_and_need_the_studio_switched_on()
    {
        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ContentStudioPlannerController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Api.Controllers.StudioRoles.Contributors, authorize.Roles);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(WhatsAppSalesAutomation.Api.Filters.RequiresFeatureAttribute)));
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));
    }

    [Fact]
    public void The_studio_tasks_are_declared_uses_of_every_real_provider_and_not_product_tasks()
    {
        foreach (var provider in AiCapabilities.RealProviders)
        {
            Assert.True(AiCapabilities.IsDeclared(provider, AiTaskKinds.ContentTopicIdeas));
            Assert.True(AiCapabilities.IsDeclared(provider, AiTaskKinds.ContentScript));
        }

        Assert.DoesNotContain(AiTaskKinds.ContentScript, AiTaskKinds.Product);
        Assert.All(AiUses.All.Where(u => AiTaskKinds.Studio.Contains(u.Task)), u => Assert.Equal(AiUses.ContentStudio, u.Area));
    }
}
