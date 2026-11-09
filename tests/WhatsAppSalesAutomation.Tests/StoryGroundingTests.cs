using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Cinematic;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Step 1 and 2 of a story: the studio gathers what the tenant has already told it (verified Business Facts), suggests video ideas from them, and writes
/// the script from them - and says plainly when there is nothing true to write from.
/// </summary>
public sealed class StoryGroundingTests : IDisposable
{
    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class PlatformContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private sealed class Metering : IStudioMeteringService
    {
        public List<string> Log { get; } = new();

        public Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default)
        {
            Log.Add("charge");
            return Task.FromResult(new StudioChargeResult(true, Guid.NewGuid(), false, null));
        }

        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default) { Log.Add("complete"); return Task.CompletedTask; }

        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default) { Log.Add("refund"); return Task.CompletedTask; }

        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class Ai : IAiTaskRunner
    {
        public AiExecutionMode Mode { get; set; } = AiExecutionMode.Live;
        public bool Success { get; set; } = true;
        public string? Text { get; set; }
        public List<AiTaskRequest> Requests { get; } = new();

        public Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AiTaskResult(
                Success, Text, Mode, Mode == AiExecutionMode.Simulator ? "Simulator" : "OpenAI", null, false, Success ? null : "Boom", Success ? null : "Provider down",
                Guid.NewGuid(), Array.Empty<AiAttempt>(), null, null, null));
        }
    }

    private sealed class Store : ITenantDocumentStore
    {
        public bool IsConfigured => true;
        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.FromResult(relativePath);
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string FolderOf(Guid tenantId) => tenantId.ToString();
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Metering _metering = new();
    private readonly Ai _ai = new();
    private readonly StoryGroundingService _grounding;
    private readonly StoryStudioService _studio;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    public StoryGroundingTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new NoUser());
        _db.Database.EnsureCreated();
        var config = Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = true })
                : throw new NotImplementedException(m.Name));
        var knowledge = new KnowledgeService(_db, config, new Clock());
        _grounding = new StoryGroundingService(knowledge, knowledge, _ai, _metering);
        _studio = new StoryStudioService(_db, config, _ai, _metering, new Store(),
            new SceneVideoProviderFactory(new ISceneVideoProvider[] { new FlowImportSceneVideoProvider() }), new Clock(), null, _grounding);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BusinessFact Fact(Guid tenant, string category, string statement, BusinessFactStatus status = BusinessFactStatus.Verified, string? subject = null)
    {
        var fact = new BusinessFact { TenantId = tenant, Category = category, Subject = subject, Statement = statement, Status = status };
        _db.BusinessFacts.Add(fact);
        _db.SaveChanges();
        return fact;
    }

    private async Task<StoryProjectDto> NewProjectAsync(string topic = "Customers wait weeks for a custom wardrobe") =>
        await _studio.CreateProjectAsync(_tenant, null, new CreateStoryProjectRequest("Idea", topic, "Hinglish", null, null, null, 30, SceneVideoProviderKind.Simulator, null, Array.Empty<Guid>()));

    private static string ScriptJson(params string[] lines) => JsonSerializer.Serialize(new CinematicScript(
        "Wardrobe in 10 days", "d", "Tired of waiting for a wardrobe?", "summary", "Hinglish", "Message us today",
        new[] { new CinematicCharacter("Customer", "a homeowner", "tired"), new CinematicCharacter("Owner", "the shop owner", "warm") },
        new[]
        {
            new CinematicScene(1, 10, "home", "wide", "cut", "", "", "p1", new[] { new CinematicDialogue("Customer", lines[0], "x", lines[0], 500, 4000) }),
            new CinematicScene(2, 10, "shop", "medium", "cut", "", "", "p2", new[] { new CinematicDialogue("Owner", lines[1], "x", lines[1], 500, 4000) }),
            new CinematicScene(3, 10, "shop", "close", "cut", "", "", "p3", new[] { new CinematicDialogue("Owner", lines[2], "x", lines[2], 500, 4000) })
        }));

    // ----- step 1: what we know -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_verified_facts_the_studio_says_it_has_nothing_to_write_from()
    {
        Fact(_tenant, "Services", "We make custom wardrobes.", BusinessFactStatus.Draft);   // not verified: does not count
        Fact(_other, "Services", "Another tenant's fact.");                                  // not theirs: never shown

        var knowledge = await _grounding.GetKnowledgeAsync(_tenant);

        Assert.False(knowledge.IsReady);
        Assert.Equal(0, knowledge.VerifiedFacts);
        Assert.Contains("verify your facts", knowledge.Message);
    }

    [Fact]
    public async Task Verified_facts_are_what_the_studio_knows_and_only_the_tenants_own()
    {
        Fact(_tenant, "Services", "We make custom wardrobes.", subject: "Wardrobes");
        Fact(_tenant, "Pricing", "Wardrobes start at 45,000 rupees.");
        Fact(_other, "Services", "Another tenant's fact.");

        var knowledge = await _grounding.GetKnowledgeAsync(_tenant);

        Assert.True(knowledge.IsReady);
        Assert.Equal(2, knowledge.VerifiedFacts);
        Assert.DoesNotContain(knowledge.Facts, f => f.Statement.Contains("Another tenant"));
        Assert.Null(knowledge.Message);
    }

    [Fact]
    public async Task Gathering_for_a_topic_tops_up_with_the_other_verified_facts_and_refuses_when_there_are_none()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _grounding.GatherAsync(_tenant, "wardrobe"));

        Fact(_tenant, "Services", "We make custom wardrobes.");
        Fact(_tenant, "Contact", "Call us on 99999 00000.");
        var gathered = await _grounding.GatherAsync(_tenant, "wardrobe");

        Assert.Equal(2, gathered.Facts.Count);   // the contact fact shares no word with the topic but is still something true to use
        Assert.Contains("only facts", gathered.PromptBlock);
    }

    // ----- step 1b: AI ideas ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ideas_are_refused_before_any_charge_or_AI_call_when_there_are_no_facts()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _grounding.SuggestIdeasAsync(_tenant, null, "Hinglish"));

        Assert.Empty(_metering.Log);
        Assert.Empty(_ai.Requests);
    }

    [Fact]
    public async Task Test_ideas_are_marked_as_test_data_and_cost_nothing()
    {
        Fact(_tenant, "Services", "We make custom wardrobes.");
        _ai.Mode = AiExecutionMode.Simulator;

        var ideas = await _grounding.SuggestIdeasAsync(_tenant, null, "Hinglish");

        Assert.All(ideas, i => Assert.StartsWith("[Test data]", i.Title));
        Assert.Equal(new[] { "charge", "refund" }, _metering.Log);
    }

    [Fact]
    public async Task Live_ideas_must_rest_on_real_fact_ids_and_an_idea_that_invents_one_is_dropped()
    {
        var real = Fact(_tenant, "Services", "We deliver custom wardrobes in 10 days.");
        _ai.Text = JsonSerializer.Serialize(new
        {
            ideas = new object[]
            {
                new { title = "Wardrobe in 10 days", problem = "Waiting weeks for a wardrobe", solution = "We deliver in 10 days", factIds = new[] { real.Id.ToString() } },
                new { title = "Made up", problem = "Invented", solution = "Invented", factIds = new[] { Guid.NewGuid().ToString() } },
                new { title = "No facts at all", problem = "p", solution = "s", factIds = Array.Empty<string>() }
            }
        });

        var ideas = await _grounding.SuggestIdeasAsync(_tenant, null, "Hinglish");

        var idea = Assert.Single(ideas);
        Assert.Equal("Wardrobe in 10 days", idea.Title);
        Assert.Equal(new[] { real.Id }, idea.FactIds);
        Assert.Equal(new[] { "charge", "complete" }, _metering.Log);
        Assert.Contains(real.Id.ToString(), _ai.Requests.Single().User);   // the model was shown the fact ids it may cite
        Assert.Equal(AiTaskKinds.CinematicIdeas, _ai.Requests.Single().Task);
    }

    [Fact]
    public async Task A_failed_or_unusable_reply_gives_the_charge_back()
    {
        Fact(_tenant, "Services", "We make custom wardrobes.");
        _ai.Success = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _grounding.SuggestIdeasAsync(_tenant, null, null));

        _ai.Success = true;
        _ai.Text = "{ not json";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _grounding.SuggestIdeasAsync(_tenant, null, null));

        Assert.Equal(new[] { "charge", "refund", "charge", "refund" }, _metering.Log);
    }

    // ----- step 2: the script ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_script_is_not_written_without_verified_facts_and_costs_nothing_then()
    {
        var project = await NewProjectAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _studio.GenerateScriptAsync(_tenant, null, project.Id));

        Assert.Contains("verify your facts", ex.Message);
        Assert.Empty(_metering.Log);
        Assert.Empty(_ai.Requests);
    }

    [Fact]
    public async Task The_script_is_written_from_the_facts_shows_which_ones_and_has_no_cast_requirement()
    {
        Fact(_tenant, "Services", "We deliver custom wardrobes in 10 days.");
        var project = await NewProjectAsync();
        _ai.Text = ScriptJson("Mujhe wardrobe chahiye.", "We deliver custom wardrobes in 10 days.", "Message us today.");

        var script = await _studio.GenerateScriptAsync(_tenant, null, project.Id);

        var prompt = _ai.Requests.Single().User;
        Assert.Contains("We deliver custom wardrobes in 10 days.", prompt);           // grounded in the fact
        Assert.Contains("customer's problem", prompt);                                 // problem -> solution -> call to action
        Assert.Contains("Customer and the business Owner", prompt);                    // no cast chosen: the model invents two plain speakers
        Assert.Equal("We deliver custom wardrobes in 10 days.", Assert.Single(script.FactsUsed!).Statement);
        Assert.Equal(AiTaskKinds.CinematicScript, _ai.Requests.Single().Task);
    }

    [Fact]
    public async Task A_risky_claim_no_fact_supports_blocks_approval_until_it_is_changed()
    {
        Fact(_tenant, "Services", "We deliver custom wardrobes in 10 days.");
        var project = await NewProjectAsync();
        _ai.Text = ScriptJson("Mujhe wardrobe chahiye.", "We guarantee 100% satisfaction and the best price.", "Message us today.");

        var script = await _studio.GenerateScriptAsync(_tenant, null, project.Id);

        var risky = Assert.Single(script.Claims!, c => c.IsRisky);
        Assert.Equal(2, risky.Scene);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => _studio.ApproveScriptAsync(_tenant, null, project.Id));
        Assert.Contains("100% satisfaction", ex.Message);

        // The tenant rewrites the line to something the facts say; the same edit keeps the facts the script rests on.
        var fixedScript = script.Script with { Scenes = script.Script.Scenes.Select(s => s.Index != 2 ? s : s with { Dialogue = new[] { s.Dialogue[0] with { Text = "We deliver custom wardrobes in 10 days." } } }).ToArray() };
        var saved = await _studio.SaveScriptAsync(_tenant, project.Id, new SaveStoryScriptRequest(fixedScript));

        Assert.DoesNotContain(saved.Claims!, c => c.IsRisky);
        Assert.Single(saved.FactsUsed!);
        var scenes = await _studio.ApproveScriptAsync(_tenant, null, project.Id);
        Assert.Equal(3, scenes.Count);
    }

    [Fact]
    public async Task A_plain_unsupported_line_is_shown_as_a_warning_but_does_not_block_approval()
    {
        Fact(_tenant, "Services", "We deliver custom wardrobes in 10 days.");
        var project = await NewProjectAsync();
        _ai.Text = ScriptJson("Kya baat hai, kitna intezaar!", "We deliver custom wardrobes in 10 days.", "Message us today.");

        var script = await _studio.GenerateScriptAsync(_tenant, null, project.Id);

        Assert.Contains(script.Claims!, c => !c.Supported && !c.IsRisky);
        Assert.Equal(3, (await _studio.ApproveScriptAsync(_tenant, null, project.Id)).Count);
    }

    [Fact]
    public async Task The_test_script_is_not_claim_checked_because_it_is_placeholder_text()
    {
        Fact(_tenant, "Services", "We make custom wardrobes.");
        var project = await NewProjectAsync();
        _ai.Mode = AiExecutionMode.Simulator;

        var script = await _studio.GenerateScriptAsync(_tenant, null, project.Id);

        Assert.True(script.IsSimulated);
        Assert.Empty(script.Claims!);
        Assert.Single(script.FactsUsed!);
        Assert.Equal(4, (await _studio.ApproveScriptAsync(_tenant, null, project.Id)).Count);   // 30 seconds as four scenes
    }
}
