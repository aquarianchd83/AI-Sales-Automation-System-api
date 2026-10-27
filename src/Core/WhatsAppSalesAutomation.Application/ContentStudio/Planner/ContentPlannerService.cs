using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Planner;

/// <summary>
/// Plans content: topic ideas, then a scene-by-scene script, each written by the platform's AI from the tenant's <i>verified</i> business facts
/// only. The AI is told to cite the facts it used, but that is a pointer, not the proof: after every write and every edit the application itself
/// checks each scene against the facts and marks it supported, unverified or unsupported, so the tenant sees what still has no fact behind it.
/// Every AI call is charged to the tenant's studio quota first and returned if the call fails, returns nothing usable, or only ran in the
/// platform's test mode. A script is a draft; nothing here approves or publishes anything.
/// </summary>
public sealed class ContentPlannerService : IContentPlannerService
{
    public const int DefaultTopicCount = 5;
    public const int MaxTopicCount = 8;
    public const int DefaultTargetSeconds = 45;
    public const int MinTargetSeconds = 15;
    public const int MaxTargetSeconds = 180;
    private const int FactsForWriter = 20;

    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _user;
    private readonly IDateTimeProvider _clock;
    private readonly IKnowledgeRetrievalService _knowledge;
    private readonly IStudioMeteringService _metering;
    private readonly IAiTaskRunner _ai;

    public ContentPlannerService(
        IApplicationDbContext db, ICurrentUserService user, IDateTimeProvider clock, IKnowledgeRetrievalService knowledge, IStudioMeteringService metering, IAiTaskRunner ai)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _knowledge = knowledge;
        _metering = metering;
        _ai = ai;
    }

    // ----- projects --------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ProjectSummaryDto>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var projects = await _db.ContentProjects.AsNoTracking().Where(p => p.TenantId == tenantId && !p.IsDeleted).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        var ids = projects.Select(p => p.Id).ToList();
        var topics = await _db.ContentTopics.AsNoTracking().Where(t => t.TenantId == tenantId && ids.Contains(t.ProjectId)).ToListAsync(ct);
        var versions = await _db.ContentVersions.AsNoTracking().Where(v => v.TenantId == tenantId && ids.Contains(v.ProjectId)).Select(v => v.ProjectId).ToListAsync(ct);

        return projects.Select(p => new ProjectSummaryDto(
            p.Id, p.Title, p.Goal, p.Language, p.Status.ToString(),
            topics.Count(t => t.ProjectId == p.Id && t.Status != ContentTopicStatus.Rejected), versions.Count(v => v == p.Id),
            topics.FirstOrDefault(t => t.ProjectId == p.Id && t.Status == ContentTopicStatus.Chosen)?.Title, p.CreatedAt, p.UpdatedAt)).ToList();
    }

    public async Task<ProjectDetailDto> CreateAsync(Guid tenantId, CreateProjectRequest request, CancellationToken ct = default)
    {
        var title = Clean(request?.Title, 200) ?? throw Invalid("title", "Give the project a title.");
        var goal = Clean(request?.Goal, 1000) ?? throw Invalid("goal", "Say what this content should achieve - the writer works from it.");
        var project = new ContentProject { TenantId = tenantId, Title = title, Goal = goal, Language = Clean(request?.Language, 40) ?? "English", CreatedBy = _user.UserId };
        _db.ContentProjects.Add(project);
        await _db.SaveChangesAsync(ct);
        return await GetAsync(tenantId, project.Id, ct);
    }

    public async Task<ProjectDetailDto> GetAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var project = await FindProjectAsync(tenantId, projectId, ct, track: false);
        var topics = await _db.ContentTopics.AsNoTracking().Where(t => t.TenantId == tenantId && t.ProjectId == projectId).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var versions = await _db.ContentVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.ProjectId == projectId).OrderByDescending(v => v.Number).ToListAsync(ct);
        var versionIds = versions.Select(v => v.Id).ToList();
        var scenes = await _db.ContentScenes.AsNoTracking().Where(s => s.TenantId == tenantId && versionIds.Contains(s.VersionId)).ToListAsync(ct);

        ScriptVersionDto? current = null;
        var currentVersion = versions.FirstOrDefault(v => v.Id == project.CurrentVersionId) ?? versions.FirstOrDefault();
        if (currentVersion is not null)
            current = await BuildVersionAsync(tenantId, currentVersion, scenes.Where(s => s.VersionId == currentVersion.Id).OrderBy(s => s.Order).ToList(), topics, ct);

        var summaries = versions.Select(v =>
        {
            var own = scenes.Where(s => s.VersionId == v.Id).ToList();
            return new ScriptVersionSummaryDto(v.Id, v.Number, topics.FirstOrDefault(t => t.Id == v.TopicId)?.Title, v.Status.ToString(), v.IsSimulated, own.Count, own.Sum(s => s.DurationSeconds), v.CreatedAt);
        }).ToList();

        return new ProjectDetailDto(
            project.Id, project.Title, project.Goal, project.Language, project.Status.ToString(), await CountUsableFactsAsync(tenantId, ct), project.CreatedAt, project.UpdatedAt,
            topics.Select(ToDto).ToList(), summaries, current);
    }

    public async Task<ProjectDetailDto> UpdateAsync(Guid tenantId, Guid projectId, UpdateProjectRequest request, CancellationToken ct = default)
    {
        var project = await FindProjectAsync(tenantId, projectId, ct);
        if (request?.Title is not null)
            project.Title = Clean(request.Title, 200) ?? throw Invalid("title", "The title cannot be empty.");
        if (request?.Goal is not null)
            project.Goal = Clean(request.Goal, 1000) ?? throw Invalid("goal", "The goal cannot be empty.");
        if (request?.Language is not null)
            project.Language = Clean(request.Language, 40) ?? "English";
        project.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await GetAsync(tenantId, projectId, ct);
    }

    public async Task DeleteAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var project = await FindProjectAsync(tenantId, projectId, ct);
        project.IsDeleted = true;
        project.DeletedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ----- topics ----------------------------------------------------------------------------------------------------------------

    public async Task<ProjectDetailDto> GenerateTopicsAsync(Guid tenantId, Guid projectId, GenerateTopicsRequest request, CancellationToken ct = default)
    {
        var project = await FindProjectAsync(tenantId, projectId, ct);
        var count = Math.Clamp(request?.Count ?? DefaultTopicCount, 1, MaxTopicCount);
        var grounding = await GroundAsync(tenantId, project.Goal, ct);
        var existing = await _db.ContentTopics.AsNoTracking().Where(t => t.TenantId == tenantId && t.ProjectId == projectId).Select(t => t.Title).ToListAsync(ct);

        var aiRequest = new AiTaskRequest(
            AiTaskKinds.ContentTopicIdeas, PlannerPrompts.TopicsSystem(project.Language), PlannerPrompts.TopicsUser(project.Goal, count, existing, grounding.PromptBlock), 1500, true, tenantId);

        var run = await RunChargedAsync(tenantId, StudioActivityKind.TopicIdeas, $"Topic ideas for \"{project.Title}\"", project.Id, aiRequest, ct);
        var parsed = PlannerReply.ParseTopics(run.Result.Text, grounding.Facts.Count, count, requireFacts: !run.Result.IsSimulated);
        if (parsed.Count == 0)
            throw await UnusableAsync(run, "The AI did not return any topic that rests on your verified facts.", ct);

        foreach (var topic in parsed)
            _db.ContentTopics.Add(new ContentTopic
            {
                TenantId = tenantId, ProjectId = projectId, Title = topic.Title, Rationale = topic.Rationale, FactIdsJson = FactIds(topic.FactNumbers, grounding),
                IsSimulated = run.Result.IsSimulated, Provider = run.Result.Provider
            });
        project.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        await FinishAsync(run, ct);
        return await GetAsync(tenantId, projectId, ct);
    }

    public async Task<ProjectDetailDto> UpdateTopicAsync(Guid tenantId, Guid topicId, UpdateTopicRequest request, CancellationToken ct = default)
    {
        var topic = await _db.ContentTopics.FirstOrDefaultAsync(t => t.Id == topicId && t.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentTopic", topicId);
        if (!await _db.ContentProjects.AnyAsync(p => p.Id == topic.ProjectId && p.TenantId == tenantId && !p.IsDeleted, ct))
            throw new NotFoundException("ContentTopic", topicId);

        if (request?.Title is not null)
            topic.Title = Clean(request.Title, 250) ?? throw Invalid("title", "The topic needs a title.");

        if (request?.Status is not null)
        {
            if (!Enum.TryParse<ContentTopicStatus>(request.Status, true, out var status))
                throw Invalid("status", "The status must be Suggested, Chosen or Rejected.");
            if (status == ContentTopicStatus.Chosen)
                foreach (var other in await _db.ContentTopics.Where(t => t.TenantId == tenantId && t.ProjectId == topic.ProjectId && t.Id != topic.Id && t.Status == ContentTopicStatus.Chosen).ToListAsync(ct))
                    other.Status = ContentTopicStatus.Suggested; // one topic is the one being written about
            topic.Status = status;
        }

        topic.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await GetAsync(tenantId, topic.ProjectId, ct);
    }

    // ----- scripts ---------------------------------------------------------------------------------------------------------------

    public async Task<ProjectDetailDto> GenerateScriptAsync(Guid tenantId, Guid projectId, GenerateScriptRequest request, CancellationToken ct = default)
    {
        var project = await FindProjectAsync(tenantId, projectId, ct);
        var topics = await _db.ContentTopics.Where(t => t.TenantId == tenantId && t.ProjectId == projectId && t.Status != ContentTopicStatus.Rejected).ToListAsync(ct);
        var topic = request?.TopicId is { } id
            ? topics.FirstOrDefault(t => t.Id == id) ?? throw new NotFoundException("ContentTopic", id)
            : topics.FirstOrDefault(t => t.Status == ContentTopicStatus.Chosen) ?? throw Invalid("topicId", "Choose a topic first - the script is written about one topic.");

        var target = Math.Clamp(request?.TargetSeconds ?? DefaultTargetSeconds, MinTargetSeconds, MaxTargetSeconds);
        var notes = Clean(request?.Notes, 1000);
        var grounding = await GroundAsync(tenantId, project.Goal + " " + topic.Title, ct);

        var aiRequest = new AiTaskRequest(
            AiTaskKinds.ContentScript, PlannerPrompts.ScriptSystem(project.Language, target), PlannerPrompts.ScriptUser(project.Goal, topic.Title, topic.Rationale, notes, grounding.PromptBlock), 3000, true, tenantId);

        var run = await RunChargedAsync(tenantId, StudioActivityKind.Script, $"Script for \"{topic.Title}\"", project.Id, aiRequest, ct);
        var parsed = PlannerReply.ParseScenes(run.Result.Text, grounding.Facts.Count);
        if (parsed.Count == 0)
            throw await UnusableAsync(run, "The AI did not return a script that could be read.", ct);

        var now = _clock.UtcNow;
        var facts = await LoadFactsAsync(tenantId, ct);
        var number = (await _db.ContentVersions.Where(v => v.TenantId == tenantId && v.ProjectId == projectId).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
        var version = new ContentVersion
        {
            TenantId = tenantId, ProjectId = projectId, Number = number, TopicId = topic.Id, TargetSeconds = target, Notes = notes, PromptVersion = PlannerPrompts.Version,
            Provider = run.Result.Provider, Model = run.Result.Model, IsSimulated = run.Result.IsSimulated, ActivityId = run.ActivityId, CreatedBy = _user.UserId
        };
        _db.ContentVersions.Add(version);

        var order = 0;
        foreach (var scene in parsed)
        {
            var outcome = PlannerClaims.Evaluate(scene.Narration, facts, now);
            _db.ContentScenes.Add(new ContentScene
            {
                TenantId = tenantId, VersionId = version.Id, Order = ++order, Narration = scene.Narration, OnScreenText = scene.OnScreenText, DurationSeconds = scene.DurationSeconds,
                VisualHint = scene.VisualHint, FactIdsJson = FactIds(scene.FactNumbers, grounding), ClaimStatus = outcome.Status, ClaimNote = outcome.Note
            });
        }

        version.ScriptText = string.Join("\n\n", parsed.Select(s => s.Narration));
        version.ContentHash = PlannerClaims.Hash(parsed.Select(s => (s.Narration, s.OnScreenText)));
        topic.Status = ContentTopicStatus.Chosen;
        project.CurrentVersionId = version.Id;
        project.Status = ContentProjectStatus.Scripted;
        project.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await FinishAsync(run, ct);
        return await GetAsync(tenantId, projectId, ct);
    }

    public async Task<ScriptVersionDto> GetVersionAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var version = await FindVersionAsync(tenantId, versionId, ct, track: false);
        return await BuildVersionAsync(tenantId, version, ct);
    }

    public async Task<ScriptVersionDto> UpdateSceneAsync(Guid tenantId, Guid sceneId, UpdateSceneRequest request, CancellationToken ct = default)
    {
        var scene = await FindSceneAsync(tenantId, sceneId, ct);
        var version = await FindEditableVersionAsync(tenantId, scene.VersionId, ct);

        var narration = Clean(request?.Narration, 2000) ?? throw Invalid("narration", "A scene needs something to say.");
        scene.Narration = narration;
        scene.OnScreenText = Clean(request?.OnScreenText, 300);
        scene.VisualHint = Clean(request?.VisualHint, 500);
        scene.DurationSeconds = Math.Clamp(request?.DurationSeconds ?? scene.DurationSeconds, PlannerReply.MinSceneSeconds, PlannerReply.MaxSceneSeconds);
        scene.EditedByTenant = true;
        scene.UpdatedAt = _clock.UtcNow;

        var outcome = PlannerClaims.Evaluate(narration, await LoadFactsAsync(tenantId, ct), _clock.UtcNow);
        scene.ClaimStatus = outcome.Status;
        scene.ClaimNote = outcome.Note;

        await SaveEditedAsync(tenantId, version, ct);
        return await BuildVersionAsync(tenantId, version, ct);
    }

    public async Task<ScriptVersionDto> DeleteSceneAsync(Guid tenantId, Guid sceneId, CancellationToken ct = default)
    {
        var scene = await FindSceneAsync(tenantId, sceneId, ct);
        var version = await FindEditableVersionAsync(tenantId, scene.VersionId, ct);
        if (await _db.ContentScenes.CountAsync(s => s.TenantId == tenantId && s.VersionId == version.Id, ct) <= 1)
            throw Invalid("sceneId", "A script needs at least one scene. Write a new script instead of removing the last one.");

        _db.ContentScenes.Remove(scene);
        await _db.SaveChangesAsync(ct);

        var order = 0;
        foreach (var s in await _db.ContentScenes.Where(s => s.TenantId == tenantId && s.VersionId == version.Id).OrderBy(s => s.Order).ToListAsync(ct))
            s.Order = ++order;
        await SaveEditedAsync(tenantId, version, ct);
        return await BuildVersionAsync(tenantId, version, ct);
    }

    public async Task<ScriptVersionDto> RecheckAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var version = await FindVersionAsync(tenantId, versionId, ct);
        var facts = await LoadFactsAsync(tenantId, ct);
        var now = _clock.UtcNow;
        foreach (var scene in await _db.ContentScenes.Where(s => s.TenantId == tenantId && s.VersionId == version.Id).ToListAsync(ct))
        {
            var outcome = PlannerClaims.Evaluate(scene.Narration, facts, now);
            scene.ClaimStatus = outcome.Status;
            scene.ClaimNote = outcome.Note;
        }

        await _db.SaveChangesAsync(ct);
        return await BuildVersionAsync(tenantId, version, ct);
    }

    // ----- AI with a charge ------------------------------------------------------------------------------------------------------

    private sealed record ChargedRun(AiTaskResult Result, Guid ActivityId);

    private async Task<ChargedRun> RunChargedAsync(Guid tenantId, StudioActivityKind kind, string description, Guid projectId, AiTaskRequest request, CancellationToken ct)
    {
        var charge = await _metering.ChargeAsync(new StudioChargeRequest(
            tenantId, kind, $"planner:{kind}:{projectId:N}:{Guid.NewGuid():N}", description, null, nameof(ContentProject), projectId.ToString(), null, _user.UserId), ct);
        if (!charge.Allowed)
            throw new ConflictException(charge.Message ?? "Your studio quota does not cover this right now.");

        AiTaskResult result;
        try
        {
            result = await _ai.RunAsync(request, ct);
        }
        catch
        {
            await _metering.RefundAsync(charge.ActivityId, "The AI call did not complete.", CancellationToken.None);
            throw;
        }

        if (!result.Success)
        {
            await _metering.RefundAsync(charge.ActivityId, "The AI call failed.", CancellationToken.None);
            throw new ConflictException($"The AI could not do this: {result.ErrorMessage ?? "the provider returned an error"} Nothing was charged.");
        }

        return new ChargedRun(result, charge.ActivityId);
    }

    /// <summary>Finishes the charge after the work was saved. Work done by the platform's test mode is predefined, so it costs the tenant nothing.</summary>
    private async Task FinishAsync(ChargedRun run, CancellationToken ct)
    {
        if (run.Result.IsSimulated)
            await _metering.RefundAsync(run.ActivityId, "The platform AI was in test mode, so nothing was charged.", ct);
        else
            await _metering.CompleteAsync(run.ActivityId, ct);
    }

    private async Task<ConflictException> UnusableAsync(ChargedRun run, string message, CancellationToken ct)
    {
        await _metering.RefundAsync(run.ActivityId, "The AI returned nothing usable.", ct);
        return new ConflictException(message + " Nothing was charged - try again.");
    }

    // ----- facts -----------------------------------------------------------------------------------------------------------------

    private async Task<GroundingResultDto> GroundAsync(Guid tenantId, string query, CancellationToken ct)
    {
        var result = await _knowledge.RetrieveAsync(tenantId, query, FactsForWriter, ct);
        if (result.Facts.Count == 0)
            result = await _knowledge.RetrieveAsync(tenantId, null, FactsForWriter, ct); // nothing matched the words: the writer still gets what is verified
        if (result.Facts.Count == 0)
            throw new ConflictException("There are no verified business facts yet. Answer the business questions and verify the facts first - the writer only uses what you have confirmed.");
        return result;
    }

    private static string FactIds(IReadOnlyList<int> numbers, GroundingResultDto grounding) =>
        JsonSerializer.Serialize(numbers.Where(n => n >= 1 && n <= grounding.Facts.Count).Select(n => grounding.Facts[n - 1].Id).Distinct().ToList());

    private async Task<List<BusinessFact>> LoadFactsAsync(Guid tenantId, CancellationToken ct) =>
        await _db.BusinessFacts.IgnoreQueryFilters().AsNoTracking().Where(f => f.TenantId == tenantId).ToListAsync(ct);

    private async Task<int> CountUsableFactsAsync(Guid tenantId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        return await _db.BusinessFacts.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(f => f.TenantId == tenantId && f.Status == BusinessFactStatus.Verified && (f.ExpiresAtUtc == null || f.ExpiresAtUtc > now), ct);
    }

    // ----- mapping and lookups ---------------------------------------------------------------------------------------------------

    private async Task<ScriptVersionDto> BuildVersionAsync(Guid tenantId, ContentVersion version, CancellationToken ct)
    {
        var scenes = await _db.ContentScenes.AsNoTracking().Where(s => s.TenantId == tenantId && s.VersionId == version.Id).OrderBy(s => s.Order).ToListAsync(ct);
        var topics = await _db.ContentTopics.AsNoTracking().Where(t => t.TenantId == tenantId && t.ProjectId == version.ProjectId).ToListAsync(ct);
        return await BuildVersionAsync(tenantId, version, scenes, topics, ct);
    }

    private async Task<ScriptVersionDto> BuildVersionAsync(Guid tenantId, ContentVersion version, IReadOnlyList<ContentScene> scenes, IReadOnlyList<ContentTopic> topics, CancellationToken ct)
    {
        var cited = scenes.SelectMany(s => ReadIds(s.FactIdsJson)).Distinct().ToList();
        var facts = cited.Count == 0
            ? new List<BusinessFact>()
            : await _db.BusinessFacts.IgnoreQueryFilters().AsNoTracking().Where(f => f.TenantId == tenantId && cited.Contains(f.Id)).ToListAsync(ct);

        var dtos = scenes.Select(s => new SceneDto(
            s.Id, s.Order, s.Narration, s.OnScreenText, s.DurationSeconds, s.VisualHint, s.ClaimStatus.ToString(), s.ClaimNote, s.EditedByTenant,
            ReadIds(s.FactIdsJson).Select(id => facts.FirstOrDefault(f => f.Id == id)).Where(f => f is not null).Select(f => new CitedFactDto(f!.Id, f.Category, f.Statement)).ToList())).ToList();

        return new ScriptVersionDto(
            version.Id, version.ProjectId, version.Number, version.TopicId, topics.FirstOrDefault(t => t.Id == version.TopicId)?.Title, version.Status.ToString(), version.TargetSeconds,
            scenes.Sum(s => s.DurationSeconds), version.Notes, version.Provider, version.Model, version.PromptVersion, version.IsSimulated, version.ActivityId, version.CreatedAt,
            scenes.Count(s => s.ClaimStatus is ClaimStatus.Supported or ClaimStatus.NoClaim),
            scenes.Count(s => s.ClaimStatus is ClaimStatus.Unverified or ClaimStatus.Unsupported), dtos);
    }

    private static TopicDto ToDto(ContentTopic t) =>
        new(t.Id, t.Title, t.Rationale, t.Status.ToString(), ReadIds(t.FactIdsJson).Count, t.IsSimulated, t.Provider, t.CreatedAt);

    private static List<Guid> ReadIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? new List<Guid>();
        }
        catch (JsonException)
        {
            return new List<Guid>();
        }
    }

    private async Task SaveEditedAsync(Guid tenantId, ContentVersion version, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
        var scenes = await _db.ContentScenes.Where(s => s.TenantId == tenantId && s.VersionId == version.Id).OrderBy(s => s.Order).ToListAsync(ct);
        version.ScriptText = string.Join("\n\n", scenes.Select(s => s.Narration));
        version.ContentHash = PlannerClaims.Hash(scenes.Select(s => (s.Narration, s.OnScreenText)));
        version.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<ContentProject> FindProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct, bool track = true)
    {
        var query = track ? _db.ContentProjects.AsQueryable() : _db.ContentProjects.AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId && !p.IsDeleted, ct) ?? throw new NotFoundException("ContentProject", projectId);
    }

    private async Task<ContentVersion> FindVersionAsync(Guid tenantId, Guid versionId, CancellationToken ct, bool track = true)
    {
        var query = track ? _db.ContentVersions.AsQueryable() : _db.ContentVersions.AsNoTracking();
        var version = await query.FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentVersion", versionId);
        if (!await _db.ContentProjects.AnyAsync(p => p.Id == version.ProjectId && p.TenantId == tenantId && !p.IsDeleted, ct))
            throw new NotFoundException("ContentVersion", versionId);
        return version;
    }

    private async Task<ContentScene> FindSceneAsync(Guid tenantId, Guid sceneId, CancellationToken ct) =>
        await _db.ContentScenes.FirstOrDefaultAsync(s => s.Id == sceneId && s.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentScene", sceneId);

    /// <summary>A script can be edited only while it is a draft: what is in review or approved is what was looked at, and changing it quietly would defeat the review.</summary>
    private async Task<ContentVersion> FindEditableVersionAsync(Guid tenantId, Guid versionId, CancellationToken ct)
    {
        var version = await FindVersionAsync(tenantId, versionId, ct);
        if (version.Status != ContentVersionStatus.Draft)
            throw new ConflictException("This script is no longer a draft, so it cannot be edited. Write a new version instead.");
        return version;
    }

    private static string? Clean(string? text, int max)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
