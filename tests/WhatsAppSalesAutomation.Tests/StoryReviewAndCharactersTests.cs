using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Subtitle corrections, final approval, usage by category, and characters generated from a script (option C).</summary>
public sealed class StoryReviewAndCharactersTests : IDisposable
{
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

    private sealed class Images : ICharacterImageProvider
    {
        public decimal? Price { get; set; } = 0.17m;
        public bool Succeed { get; set; } = true;
        public int Calls { get; private set; }
        public List<string> Prompts { get; } = new();
        public string Name => "Fake";

        public decimal? EstimateCostUsd() => Price;

        public Task<CharacterImageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Calls++;
            Prompts.Add(prompt);
            return Task.FromResult(Succeed ? new CharacterImageResult(true, new byte[] { 1, 2, 3 }, Price, null) : new CharacterImageResult(false, null, null, "Image service down"));
        }
    }

    private sealed class Store : ITenantDocumentStore
    {
        public List<string> Saved { get; } = new();
        public bool IsConfigured => true;

        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            Saved.Add(relativePath);
            return Task.FromResult($"{tenantId}/{relativePath}");
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string FolderOf(Guid tenantId) => tenantId.ToString();
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Metering _metering = new();
    private readonly Images _images = new();
    private readonly Store _store = new();
    private readonly StoryReviewService _review;
    private readonly StoryCharacterGenerationService _generation;
    private readonly Guid _tenant = Guid.NewGuid();

    public StoryReviewAndCharactersTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new NoUser());
        _db.Database.EnsureCreated();
        var config = Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = true })
                : throw new NotImplementedException(m.Name));
        _review = new StoryReviewService(_db, config);
        _generation = new StoryCharacterGenerationService(_db, config, _images, _metering, _store);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CinematicScript Script() => new(
        "T", "d", "hook", "summary", "Hinglish", "end",
        new[] { new CinematicCharacter("Guru Ji", "Elderly sage, white beard", "calm"), new CinematicCharacter("Ramesh", "Angry husband", "loud") },
        new[]
        {
            new CinematicScene(1, 10, "s", "c", "t", "f", "k", "p1", new[] { new CinematicDialogue("Guru Ji", "Shaant", "x", "Shaant", 500, 3000) }),
            new CinematicScene(2, 10, "s", "c", "t", "f", "k", "p2", new[] { new CinematicDialogue("Ramesh", "Kya?", "x", "Kya?", 500, 3000) }),
            new CinematicScene(3, 10, "s", "c", "t", "f", "k", "p3", new[] { new CinematicDialogue("Guru Ji", "Chai", "x", "Chai", 500, 3000) })
        });

    private (StoryProject Project, StoryScene Scene, StoryDialogueLine Line) AddProject(StoryProjectStatus status, string versionHash = "h1")
    {
        var script = new StoryScriptVersion
        {
            TenantId = _tenant, Number = 1, Json = JsonSerializer.Serialize(Script()), ContentHash = versionHash, Source = "Simulator", Status = StoryScriptStatus.Approved
        };
        var project = new StoryProject
        {
            TenantId = _tenant, Title = "t", Topic = "t", Language = "Hinglish", Status = status, CurrentScriptVersionId = script.Id, TargetSeconds = 30, BudgetUsd = 10m
        };
        script.ProjectId = project.Id;
        var scene = new StoryScene { TenantId = _tenant, ProjectId = project.Id, ScriptVersionId = script.Id, Index = 1, TargetSeconds = 10, VideoPrompt = "p", Status = StorySceneStatus.Completed };
        var line = new StoryDialogueLine { TenantId = _tenant, SceneId = scene.Id, Order = 1, SpeakerName = "Guru Ji", Text = "Shaant", StartMs = 500, EndMs = 3000 };
        _db.StoryProjects.Add(project);
        _db.StoryScriptVersions.Add(script);
        _db.StoryScenes.Add(scene);
        _db.StoryDialogueLines.Add(line);
        _db.SaveChanges();
        return (project, scene, line);
    }

    private StoryRender AddRender(Guid projectId, string hash, StoryJobStatus status = StoryJobStatus.Completed)
    {
        var render = new StoryRender { TenantId = _tenant, ProjectId = projectId, Status = status, ContentHash = hash, CreatedAt = DateTime.UtcNow.AddMinutes(-5) };
        _db.StoryRenders.Add(render);
        _db.SaveChanges();
        render.CreatedAt = DateTime.UtcNow.AddMinutes(-5);   // the save stamps "now"; the render is older than any correction made next
        _db.SaveChanges();
        return render;
    }

    // ----- subtitle corrections and final approval -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_subtitle_can_be_corrected_after_approval_but_not_on_a_draft_or_an_approved_video()
    {
        var (project, scene, _) = AddProject(StoryProjectStatus.InReview);

        var changed = await _review.EditDialogueAsync(_tenant, scene.Id, 1, "  शांत हो जाओ  ");
        Assert.Equal("शांत हो जाओ", changed.Dialogue[0].Text);
        await Assert.ThrowsAsync<ArgumentException>(() => _review.EditDialogueAsync(_tenant, scene.Id, 1, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => _review.EditDialogueAsync(_tenant, scene.Id, 1, new string('x', 201)));
        await Assert.ThrowsAsync<NotFoundException>(() => _review.EditDialogueAsync(_tenant, scene.Id, 9, "x"));
        await Assert.ThrowsAsync<NotFoundException>(() => _review.EditDialogueAsync(Guid.NewGuid(), scene.Id, 1, "x"));

        project.Status = StoryProjectStatus.Draft;
        _db.SaveChanges();
        await Assert.ThrowsAsync<ConflictException>(() => _review.EditDialogueAsync(_tenant, scene.Id, 1, "x"));
        project.Status = StoryProjectStatus.Approved;
        _db.SaveChanges();
        await Assert.ThrowsAsync<ConflictException>(() => _review.EditDialogueAsync(_tenant, scene.Id, 1, "x"));
    }

    [Fact]
    public async Task Final_approval_needs_a_finished_render_of_this_script()
    {
        var (project, _, _) = AddProject(StoryProjectStatus.InReview, "h1");

        await Assert.ThrowsAsync<ConflictException>(() => _review.ApproveFinalAsync(_tenant, null, project.Id));   // no render yet

        AddRender(project.Id, "OLD-SCRIPT");
        await Assert.ThrowsAsync<ConflictException>(() => _review.ApproveFinalAsync(_tenant, null, project.Id));   // render of another script
    }

    [Fact]
    public async Task A_correction_made_after_the_render_must_be_rendered_before_approval()
    {
        var (project, scene, _) = AddProject(StoryProjectStatus.InReview, "h1");
        AddRender(project.Id, "h1");
        await _review.EditDialogueAsync(_tenant, scene.Id, 1, "Corrected");

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _review.ApproveFinalAsync(_tenant, null, project.Id));
        Assert.Contains("corrected", ex.Message);
    }

    [Fact]
    public async Task A_good_render_lets_the_video_be_approved_and_locks_further_corrections()
    {
        var (project, scene, _) = AddProject(StoryProjectStatus.InReview, "h1");
        AddRender(project.Id, "h1");

        var approved = await _review.ApproveFinalAsync(_tenant, null, project.Id);

        Assert.Equal(StoryProjectStatus.Approved, approved.Status);
        await Assert.ThrowsAsync<ConflictException>(() => _review.EditDialogueAsync(_tenant, scene.Id, 1, "late"));
        await Assert.ThrowsAsync<ConflictException>(() => _review.ApproveFinalAsync(_tenant, null, project.Id));
    }

    [Fact]
    public async Task Usage_is_split_by_kind_and_shows_what_failed_attempts_may_have_cost()
    {
        var (project, scene, _) = AddProject(StoryProjectStatus.InReview);
        _db.StoryGenerationJobs.Add(new StoryGenerationJob { TenantId = _tenant, ProjectId = project.Id, SceneId = scene.Id, Kind = StoryJobKind.SceneClip, Status = StoryJobStatus.Completed, IdempotencyKey = "a", EstimatedCostUsd = 0.96m, ActualCostUsd = 0.96m });
        _db.StoryGenerationJobs.Add(new StoryGenerationJob { TenantId = _tenant, ProjectId = project.Id, SceneId = scene.Id, Kind = StoryJobKind.SceneClip, Status = StoryJobStatus.Failed, IdempotencyKey = "b", EstimatedCostUsd = 0.96m });
        _db.StoryGenerationJobs.Add(new StoryGenerationJob { TenantId = _tenant, ProjectId = project.Id, Kind = StoryJobKind.CharacterImage, Status = StoryJobStatus.Completed, IdempotencyKey = "c", EstimatedCostUsd = 0.17m, ActualCostUsd = 0.17m });
        _db.SaveChanges();

        var usage = await _review.GetUsageAsync(_tenant, project.Id);

        Assert.Equal(2.09m, usage.TotalUsd);
        Assert.Equal(0.96m, usage.WastedOnFailedUsd);
        Assert.Equal(10m, usage.BudgetUsd);
        var clips = usage.Lines.Single(l => l.Category == "Scene clips");
        Assert.Equal((2, 1), (clips.Jobs, clips.Failed));
        await Assert.ThrowsAsync<NotFoundException>(() => _review.GetUsageAsync(Guid.NewGuid(), project.Id));
    }

    // ----- option C: characters from the script --------------------------------------------------------------------------------------------------

    private async Task<StoryProject> DraftWithCastAsync(params string[] castNames)
    {
        var (project, _, _) = AddProject(StoryProjectStatus.Draft);
        foreach (var name in castNames)
        {
            var c = new StoryCharacter { TenantId = _tenant, Name = name };
            _db.StoryCharacters.Add(c);
            _db.StoryProjectCharacters.Add(new StoryProjectCharacter { TenantId = _tenant, ProjectId = project.Id, CharacterId = c.Id });
        }

        await _db.SaveChangesAsync();
        return project;
    }

    [Fact]
    public async Task Only_the_characters_missing_from_the_cast_are_generated_and_each_waits_for_approval()
    {
        var project = await DraftWithCastAsync("guru ji");   // already chosen, matched by name regardless of case

        var made = await _generation.GenerateForScriptAsync(_tenant, null, project.Id, new GenerateCharactersRequest(true));

        var only = Assert.Single(made);
        Assert.Equal("Ramesh", only.Character.Name);
        Assert.Equal(StoryCharacterOrigin.ScriptGenerated, only.Character.Origin);
        Assert.Equal(StoryReferenceStatus.Generated, only.Preview.Status);
        Assert.Equal(1, _images.Calls);
        Assert.Contains("Angry husband", _images.Prompts[0]);
        Assert.Equal(2, await _db.StoryProjectCharacters.IgnoreQueryFilters().CountAsync(l => l.ProjectId == project.Id));
        Assert.Equal(StoryReferenceStatus.Generated, (await _db.StoryCharacterReferences.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_cast_that_already_covers_the_script_costs_nothing()
    {
        var project = await DraftWithCastAsync("Guru Ji", "Ramesh");

        Assert.Empty(await _generation.GenerateForScriptAsync(_tenant, null, project.Id, new GenerateCharactersRequest(false)));
        Assert.Equal(0, _images.Calls);
        Assert.Empty(_metering.Log);
    }

    [Fact]
    public async Task Paid_pictures_need_the_cost_confirmed_first()
    {
        var project = await DraftWithCastAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _generation.GenerateForScriptAsync(_tenant, null, project.Id, new GenerateCharactersRequest(false)));
        Assert.Contains("0.34", ex.Message);
        Assert.Equal(0, _images.Calls);
        Assert.Empty(await _db.StoryCharacters.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Free_placeholder_pictures_need_no_confirmation_and_no_charge()
    {
        _images.Price = 0m;
        var project = await DraftWithCastAsync();

        var made = await _generation.GenerateForScriptAsync(_tenant, null, project.Id, new GenerateCharactersRequest(false));

        Assert.Equal(2, made.Count);
        Assert.Empty(_metering.Log);
    }

    [Fact]
    public async Task A_failed_picture_is_refunded_and_saves_no_reference()
    {
        _images.Succeed = false;
        var project = await DraftWithCastAsync("Guru Ji");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _generation.GenerateForScriptAsync(_tenant, null, project.Id, new GenerateCharactersRequest(true)));

        Assert.Equal(new[] { "charge", "refund" }, _metering.Log);
        Assert.Empty(await _db.StoryCharacterReferences.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_revision_is_a_new_version_and_never_overwrites_the_approved_picture()
    {
        var project = await DraftWithCastAsync();
        var made = await _generation.GenerateForScriptAsync(_tenant, null, project.Id, new GenerateCharactersRequest(true));
        var character = made[0].Character;
        var approved = await _db.StoryCharacterReferences.IgnoreQueryFilters().FirstAsync(r => r.CharacterId == character.Id);
        approved.Status = StoryReferenceStatus.Approved;
        await _db.SaveChangesAsync();

        var revised = await _generation.GenerateReferenceAsync(_tenant, null, character.Id, new GenerateReferenceRequest("older, with glasses", true));

        Assert.Equal(2, revised.Version);
        Assert.Equal(StoryReferenceStatus.Generated, revised.Status);
        Assert.Contains("older, with glasses", _images.Prompts.Last());
        Assert.Equal(StoryReferenceStatus.Approved, (await _db.StoryCharacterReferences.IgnoreQueryFilters().SingleAsync(r => r.Id == approved.Id)).Status);
    }

    [Fact]
    public async Task Characters_cannot_be_generated_before_a_script_exists_or_for_another_tenant()
    {
        var project = new StoryProject { TenantId = _tenant, Title = "t", Topic = "t", Language = "Hinglish", Status = StoryProjectStatus.Draft };
        _db.StoryProjects.Add(project);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => _generation.GenerateForScriptAsync(_tenant, null, project.Id, new GenerateCharactersRequest(true)));
        await Assert.ThrowsAsync<NotFoundException>(() => _generation.GenerateForScriptAsync(Guid.NewGuid(), null, project.Id, new GenerateCharactersRequest(true)));
    }
}
