using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

/// <summary>See <see cref="IStoryStudioService"/>. Every query names its tenant explicitly, like the rest of the studio.</summary>
public class StoryStudioService : IStoryStudioService
{
    public const long MaxImageBytes = 8 * 1024 * 1024;
    public const int MinImageSide = 512;
    public const int SceneSeconds = 8;

    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly IAiTaskRunner _ai;
    private readonly IStudioMeteringService _metering;
    private readonly ITenantDocumentStore _store;
    private readonly ISceneVideoProviderFactory _providers;
    private readonly IDateTimeProvider _clock;

    public StoryStudioService(
        IApplicationDbContext context, ITenantConfigOverrideProvider config, IAiTaskRunner ai, IStudioMeteringService metering,
        ITenantDocumentStore store, ISceneVideoProviderFactory providers, IDateTimeProvider clock)
    {
        _context = context;
        _config = config;
        _ai = ai;
        _metering = metering;
        _store = store;
        _providers = providers;
        _clock = clock;
    }

    // ----- characters --------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<StoryCharacterDto>> ListCharactersAsync(Guid tenantId, string? search, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);

        // The tenant's own characters, plus other tenants' characters that were explicitly shared. A private character of another tenant never appears.
        var query = _context.StoryCharacters.IgnoreQueryFilters()
            .Where(c => !c.IsArchived && (c.TenantId == tenantId || c.Visibility == StoryCharacterVisibility.Shared));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(c => c.Name.Contains(term) || (c.Description != null && c.Description.Contains(term)));
        }

        var rows = await query.OrderBy(c => c.Name).Take(200).ToListAsync(ct);
        var ids = rows.Select(c => c.Id).ToList();
        var refs = await _context.StoryCharacterReferences.IgnoreQueryFilters().Where(r => ids.Contains(r.CharacterId)).ToListAsync(ct);
        return rows.Select(c => ToDto(c, tenantId, refs.Where(r => r.CharacterId == c.Id).ToList())).ToList();
    }

    public async Task<StoryCharacterDto> CreateCharacterAsync(Guid tenantId, Guid? userId, CreateStoryCharacterRequest request, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 150) throw new ArgumentException("A character needs a name of up to 150 characters.");

        var character = new StoryCharacter
        {
            TenantId = tenantId,
            Name = name,
            Description = Clip(request.Description, 2000),
            Personality = Clip(request.Personality, 1000),
            VisualAttributes = Clip(request.VisualAttributes, 2000),
            VoicePreferences = Clip(request.VoicePreferences, 500),
            Origin = request.Origin,
            CreatedBy = userId
        };
        _context.StoryCharacters.Add(character);
        await _context.SaveChangesAsync(ct);
        return ToDto(character, tenantId, new List<StoryCharacterReference>());
    }

    public async Task ArchiveCharacterAsync(Guid tenantId, Guid characterId, CancellationToken ct = default)
    {
        var character = await OwnedCharacterAsync(tenantId, characterId, ct);
        character.IsArchived = true;
        await _context.SaveChangesAsync(ct);
    }

    public async Task<StoryReferenceDto> AddReferenceAsync(
        Guid tenantId, Guid? userId, Guid characterId, Stream content, string fileName, bool rightsConfirmed, CancellationToken ct = default)
    {
        var character = await OwnedCharacterAsync(tenantId, characterId, ct);
        if (!rightsConfirmed) throw new ArgumentException("Confirm that you have the rights to use this image before uploading it.");
        if (!_store.IsConfigured) throw new StorageUnavailableException("File storage is not set up yet, so images cannot be uploaded.");

        var bytes = await ReadLimitedAsync(content, ct);
        var image = ImageSniffer.Inspect(bytes) ?? throw new ArgumentException("Only PNG, JPEG or WebP images are accepted.");
        if (image.Width is { } w && image.Height is { } h && Math.Min(w, h) < MinImageSide)
            throw new ArgumentException($"The image is {w}x{h}; at least {MinImageSide} pixels on the short side are needed for a usable character reference.");

        var existing = await _context.StoryCharacterReferences.IgnoreQueryFilters().Where(r => r.CharacterId == characterId).ToListAsync(ct);
        var version = existing.Count == 0 ? 1 : existing.Max(r => r.Version) + 1;

        using var stream = new MemoryStream(bytes);
        var key = await _store.SaveAsync(tenantId, $"stories/characters/{characterId}/v{version}.{image.Extension}", stream, image.ContentType, ct);

        var reference = new StoryCharacterReference
        {
            TenantId = tenantId,
            CharacterId = characterId,
            StorageKey = key,
            ContentType = image.ContentType,
            SizeBytes = bytes.Length,
            Width = image.Width,
            Height = image.Height,
            Checksum = Convert.ToHexString(SHA256.HashData(bytes)),
            Status = StoryReferenceStatus.Uploaded,
            Version = version
        };
        _context.StoryCharacterReferences.Add(reference);

        character.RightsConfirmed = true;
        character.RightsConfirmedBy = userId;
        character.RightsConfirmedAtUtc = _clock.UtcNow;
        await _context.SaveChangesAsync(ct);
        return ToDto(reference);
    }

    public async Task<StoryReferenceDto> ApproveReferenceAsync(Guid tenantId, Guid characterId, Guid referenceId, CancellationToken ct = default)
    {
        await OwnedCharacterAsync(tenantId, characterId, ct);
        var all = await _context.StoryCharacterReferences.IgnoreQueryFilters().Where(r => r.CharacterId == characterId && r.TenantId == tenantId).ToListAsync(ct);
        var chosen = all.FirstOrDefault(r => r.Id == referenceId) ?? throw new NotFoundException("Reference image", referenceId);

        // The previously approved image is superseded, never overwritten or deleted: old projects keep what they were made from.
        foreach (var other in all.Where(r => r.Status == StoryReferenceStatus.Approved && r.Id != referenceId))
            other.Status = StoryReferenceStatus.Superseded;
        chosen.Status = StoryReferenceStatus.Approved;
        await _context.SaveChangesAsync(ct);
        return ToDto(chosen);
    }

    // ----- projects ----------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<StoryProjectDto>> ListProjectsAsync(Guid tenantId, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var projects = await _context.StoryProjects.IgnoreQueryFilters().Where(p => p.TenantId == tenantId).OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
        var ids = projects.Select(p => p.Id).ToList();
        var links = await _context.StoryProjectCharacters.IgnoreQueryFilters().Where(l => ids.Contains(l.ProjectId)).ToListAsync(ct);
        return projects.Select(p => ToDto(p, links.Where(l => l.ProjectId == p.Id).Select(l => l.CharacterId).ToList())).ToList();
    }

    public async Task<StoryProjectDto> CreateProjectAsync(Guid tenantId, Guid? userId, CreateStoryProjectRequest request, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Topic)) throw new ArgumentException("A project needs a title and a topic.");
        if (request.TargetSeconds is < 8 or > 40) throw new ArgumentException("The target length must be between 8 and 40 seconds.");
        if (request.BudgetUsd is < 0) throw new ArgumentException("The budget cannot be negative.");

        var characterIds = request.CharacterIds.Distinct().ToList();
        // Only characters this tenant may use: its own, or explicitly shared ones.
        var usable = await _context.StoryCharacters.IgnoreQueryFilters()
            .Where(c => characterIds.Contains(c.Id) && !c.IsArchived && (c.TenantId == tenantId || c.Visibility == StoryCharacterVisibility.Shared))
            .Select(c => c.Id).ToListAsync(ct);
        if (usable.Count != characterIds.Count) throw new NotFoundException("One of the selected characters was not found.");

        var project = new StoryProject
        {
            TenantId = tenantId,
            Title = request.Title.Trim(),
            Topic = request.Topic.Trim(),
            Language = string.IsNullOrWhiteSpace(request.Language) ? "Hinglish" : request.Language.Trim(),
            Style = Clip(request.Style, 200),
            Audience = Clip(request.Audience, 200),
            Moral = Clip(request.Moral, 500),
            TargetSeconds = request.TargetSeconds,
            VideoProvider = request.VideoProvider,
            BudgetUsd = request.BudgetUsd,
            Status = StoryProjectStatus.Draft,
            CreatedBy = userId
        };
        _context.StoryProjects.Add(project);
        foreach (var id in usable) _context.StoryProjectCharacters.Add(new StoryProjectCharacter { TenantId = tenantId, ProjectId = project.Id, CharacterId = id });
        await _context.SaveChangesAsync(ct);
        return ToDto(project, usable);
    }

    public async Task<StoryProjectDto> GetProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        return ToDto(project, await CharacterIdsAsync(projectId, ct));
    }

    public async Task<StoryProjectDto> SetVideoProviderAsync(Guid tenantId, Guid projectId, SceneVideoProviderKind provider, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        if (project.Status is StoryProjectStatus.Generating) throw new ConflictException("Clips are being generated; the provider cannot be changed now.");
        project.VideoProvider = provider;
        await _context.SaveChangesAsync(ct);
        return ToDto(project, await CharacterIdsAsync(projectId, ct));
    }

    // ----- scripts -----------------------------------------------------------------------------------------------------------------

    public async Task<StoryScriptVersionDto> GenerateScriptAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        if (project.Status != StoryProjectStatus.Draft) throw new ConflictException("The script is already approved. Start a new project to change it.");

        var dto = ToDto(project, await CharacterIdsAsync(projectId, ct));
        var cast = (await ListCharactersAsync(tenantId, null, ct)).Where(c => dto.CharacterIds.Contains(c.Id)).ToList();
        var sceneCount = Math.Clamp((int)Math.Ceiling(project.TargetSeconds / (double)SceneSeconds), 1, CinematicScriptValidator.MaxScenes);
        var perScene = (int)Math.Ceiling(project.TargetSeconds / (double)sceneCount);

        var charge = await _metering.ChargeAsync(new StudioChargeRequest(
            tenantId, StudioActivityKind.Script, $"story-script:{projectId}:{Guid.NewGuid():N}", $"Story script for '{project.Title}'",
            ReferenceType: nameof(StoryProject), ReferenceId: projectId.ToString(), ActorUserId: userId), ct);
        if (!charge.Allowed) throw new PlanLimitExceededException(charge.Message ?? "Your content generation quota is used up.");

        try
        {
            var result = await _ai.RunAsync(new AiTaskRequest(
                AiTaskKinds.CinematicScript, StoryPrompts.System, StoryPrompts.User(dto, cast, sceneCount, perScene), 3500, ExpectJson: true,
                TenantId: tenantId, Feature: AiFeatures.ContentGeneration), ct);

            CinematicScript? script;
            string source;
            if (result.IsSimulated)
            {
                // The simulator's generic reply is not a script. Use the clearly marked test script instead; it costs nothing, so the charge is returned.
                script = SimulatedScriptWriter.Write(dto, cast, sceneCount, perScene);
                source = "Simulator";
                await _metering.RefundAsync(charge.ActivityId, "Test data: no AI provider was used.", ct);
            }
            else if (!result.Success)
            {
                await _metering.RefundAsync(charge.ActivityId, result.ErrorMessage ?? "The AI provider failed.", ct);
                throw new InvalidOperationException(result.ErrorMessage ?? "The AI provider could not write the script. Nothing was charged.");
            }
            else
            {
                var checkedScript = CinematicScriptValidator.Validate(result.Text, project.TargetSeconds);
                if (!checkedScript.IsValid)
                {
                    await _metering.RefundAsync(charge.ActivityId, "The AI reply did not meet the script rules.", ct);
                    throw new InvalidOperationException("The AI wrote a script that broke the rules, so it was not saved and you were not charged: " + string.Join(" ", checkedScript.Errors.Take(3)));
                }

                script = checkedScript.Script;
                source = result.Provider;
                await _metering.CompleteAsync(charge.ActivityId, ct);
            }

            return await SaveVersionAsync(project, script!, source, ct);
        }
        catch (OperationCanceledException)
        {
            await _metering.RefundAsync(charge.ActivityId, "Cancelled before a script was saved.", CancellationToken.None);
            throw;
        }
    }

    public async Task<StoryScriptVersionDto> SaveScriptAsync(Guid tenantId, Guid projectId, SaveStoryScriptRequest request, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        if (project.Status != StoryProjectStatus.Draft) throw new ConflictException("The script is already approved and can no longer be edited.");

        var json = JsonSerializer.Serialize(request.Script);
        var result = CinematicScriptValidator.Validate(json, project.TargetSeconds);
        if (!result.IsValid) throw new ArgumentException(string.Join(" ", result.Errors));
        return await SaveVersionAsync(project, result.Script!, "Edited", ct);
    }

    public async Task<StoryScriptVersionDto?> GetCurrentScriptAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        if (project.CurrentScriptVersionId is not { } id) return null;
        var version = await _context.StoryScriptVersions.IgnoreQueryFilters().FirstAsync(v => v.Id == id, ct);
        return ToDto(version);
    }

    public async Task<IReadOnlyList<StorySceneDto>> ApproveScriptAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        if (project.Status != StoryProjectStatus.Draft) throw new ConflictException("The script is already approved.");
        var versionDto = await GetCurrentScriptAsync(tenantId, projectId, ct) ?? throw new ConflictException("There is no script to approve yet.");
        var version = await _context.StoryScriptVersions.IgnoreQueryFilters().FirstAsync(v => v.Id == versionDto.Id, ct);

        // Re-validate the stored text: approval is the gate before paid work, so it never trusts what was checked earlier.
        var result = CinematicScriptValidator.Validate(version.Json, project.TargetSeconds);
        if (!result.IsValid) throw new ConflictException("The script no longer meets the rules: " + string.Join(" ", result.Errors));

        var script = result.Script!;
        var characters = await _context.StoryCharacters.IgnoreQueryFilters()
            .Where(c => _context.StoryProjectCharacters.IgnoreQueryFilters().Where(l => l.ProjectId == projectId).Select(l => l.CharacterId).Contains(c.Id)).ToListAsync(ct);

        var scenes = new List<StoryScene>();
        foreach (var s in script.Scenes)
        {
            var scene = new StoryScene
            {
                TenantId = tenantId,
                ProjectId = projectId,
                ScriptVersionId = version.Id,
                Index = s.Index,
                TargetSeconds = s.TargetSeconds,
                VideoPrompt = s.VideoPrompt,
                Status = StorySceneStatus.Pending
            };
            scenes.Add(scene);
            _context.StoryScenes.Add(scene);
            var order = 0;
            foreach (var d in (s.Dialogue ?? Array.Empty<CinematicDialogue>()).OrderBy(x => x.StartMs))
            {
                _context.StoryDialogueLines.Add(new StoryDialogueLine
                {
                    TenantId = tenantId,
                    SceneId = scene.Id,
                    Order = ++order,
                    SpeakerName = d.Speaker.Trim(),
                    CharacterId = characters.FirstOrDefault(c => string.Equals(c.Name, d.Speaker.Trim(), StringComparison.OrdinalIgnoreCase))?.Id,
                    Text = d.Text,
                    StartMs = d.StartMs,
                    EndMs = d.EndMs
                });
            }
        }

        version.Status = StoryScriptStatus.Approved;
        version.ApprovedBy = userId;
        version.ApprovedAtUtc = _clock.UtcNow;
        project.Status = StoryProjectStatus.ScriptApproved;
        await _context.SaveChangesAsync(ct);

        return scenes.Select(sc => new StorySceneDto(sc.Id, sc.Index, sc.TargetSeconds, sc.Status, sc.VideoPrompt,
            script.Scenes.First(x => x.Index == sc.Index).Dialogue.Select(d => new StoryDialogueDto(d.Speaker, d.Text, d.StartMs, d.EndMs)).ToList())).ToList();
    }

    public async Task<StoryCostEstimateDto> EstimateCostAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        var provider = _providers.For(project.VideoProvider);
        var clips = Math.Clamp((int)Math.Ceiling(project.TargetSeconds / (double)SceneSeconds), 1, CinematicScriptValidator.MaxScenes);
        var perClip = provider.EstimateCostUsd(SceneSeconds);
        return perClip is { } price
            ? new StoryCostEstimateDto(true, price, SceneSeconds, clips, project.VideoProvider.ToString(),
                $"About {price * clips:0.00} USD for {clips} clips if every clip works first time. Regenerating a clip costs {price:0.00} USD again. This is an estimate, not billing data.")
            : new StoryCostEstimateDto(false, 0m, SceneSeconds, clips, project.VideoProvider.ToString(), "This provider does not charge per clip through us (Flow is generated by you in Flow).");
    }

    // ----- helpers -----------------------------------------------------------------------------------------------------------------

    private async Task<StoryScriptVersionDto> SaveVersionAsync(StoryProject project, CinematicScript script, string source, CancellationToken ct)
    {
        var previous = await _context.StoryScriptVersions.IgnoreQueryFilters().Where(v => v.ProjectId == project.Id).ToListAsync(ct);
        foreach (var old in previous.Where(v => v.Status == StoryScriptStatus.Draft)) old.Status = StoryScriptStatus.Superseded;

        var version = new StoryScriptVersion
        {
            TenantId = project.TenantId,
            ProjectId = project.Id,
            Number = previous.Count == 0 ? 1 : previous.Max(v => v.Number) + 1,
            Json = JsonSerializer.Serialize(script),
            ContentHash = CinematicScriptValidator.ContentHash(script),
            Status = StoryScriptStatus.Draft,
            Source = source
        };
        _context.StoryScriptVersions.Add(version);
        project.CurrentScriptVersionId = version.Id;
        await _context.SaveChangesAsync(ct);
        return ToDto(version);
    }

    private async Task<StoryProject> ProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        return await _context.StoryProjects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Story project", projectId);
    }

    private async Task<StoryCharacter> OwnedCharacterAsync(Guid tenantId, Guid characterId, CancellationToken ct)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        // Editing is for the owner only; a shared character can be used but not changed by another tenant.
        return await _context.StoryCharacters.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == characterId && c.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Character", characterId);
    }

    private Task<List<Guid>> CharacterIdsAsync(Guid projectId, CancellationToken ct) =>
        _context.StoryProjectCharacters.IgnoreQueryFilters().Where(l => l.ProjectId == projectId).Select(l => l.CharacterId).ToListAsync(ct);

    private static async Task<byte[]> ReadLimitedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxImageBytes) throw new ArgumentException($"The image is larger than {MaxImageBytes / 1024 / 1024} MB.");
        }

        return buffer.ToArray();
    }

    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    private static StoryCharacterDto ToDto(StoryCharacter c, Guid tenantId, IReadOnlyList<StoryCharacterReference> refs) => new(
        c.Id, c.Name, c.Description, c.Personality, c.VisualAttributes, c.VoicePreferences, c.Origin, c.Visibility, c.TenantId == tenantId, c.RightsConfirmed,
        refs.FirstOrDefault(r => r.Status == StoryReferenceStatus.Approved)?.Id, refs.Count);

    private static StoryReferenceDto ToDto(StoryCharacterReference r) => new(r.Id, r.Version, r.Status, r.ContentType, r.SizeBytes, r.Width, r.Height);

    private static StoryProjectDto ToDto(StoryProject p, IReadOnlyList<Guid> characterIds) => new(
        p.Id, p.Title, p.Topic, p.Language, p.Style, p.Audience, p.Moral, p.TargetSeconds, p.Status, p.VideoProvider, p.BudgetUsd, p.CurrentScriptVersionId,
        characterIds, p.CreatedAt);

    private static StoryScriptVersionDto ToDto(StoryScriptVersion v)
    {
        var script = JsonSerializer.Deserialize<CinematicScript>(v.Json, StoryPrompts.Json)!;
        return new StoryScriptVersionDto(v.Id, v.Number, v.Status, v.Source, v.ContentHash, script, v.Source == "Simulator", v.CreatedAt);
    }
}

/// <summary>Identifies an upload by its first bytes, not by its file name or the type the client claims, and reads its size when cheap to do so.</summary>
public static class ImageSniffer
{
    public sealed record Info(string ContentType, string Extension, int? Width, int? Height);

    public static Info? Inspect(byte[] b)
    {
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return new Info("image/png", "png", ReadInt(b, 16), ReadInt(b, 20));

        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var (w, h) = JpegSize(b);
            return new Info("image/jpeg", "jpg", w, h);
        }

        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return new Info("image/webp", "webp", null, null);

        return null;
    }

    private static int ReadInt(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    private static (int? W, int? H) JpegSize(byte[] b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        }

        return (null, null);
    }
}
