using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

public sealed record CharacterImageResult(bool Success, byte[]? Png, decimal? CostUsd, string? Error);

/// <summary>Makes a character picture from a prompt. The simulator draws a plain placeholder for free; the OpenAI adapter makes a real one and costs money.</summary>
public interface ICharacterImageProvider
{
    string Name { get; }

    decimal? EstimateCostUsd();

    Task<CharacterImageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default);
}

public sealed record GenerateCharactersRequest(bool ConfirmCost);

public sealed record GenerateReferenceRequest(string? Instruction, bool ConfirmCost);

public sealed record GeneratedCharacterDto(StoryCharacterDto Character, StoryReferenceDto Preview);

public interface IStoryCharacterGenerationService
{
    /// <summary>Option C. For each character the script needs that is not already in the cast, makes a new character with a preview picture for the user to approve.
    /// A character the user already chose is never regenerated.</summary>
    Task<IReadOnlyList<GeneratedCharacterDto>> GenerateForScriptAsync(Guid tenantId, Guid? userId, Guid projectId, GenerateCharactersRequest request, CancellationToken ct = default);

    /// <summary>Makes another preview for one character, optionally following an instruction ("older, with a beard"). The approved picture is never overwritten.</summary>
    Task<StoryReferenceDto> GenerateReferenceAsync(Guid tenantId, Guid? userId, Guid characterId, GenerateReferenceRequest request, CancellationToken ct = default);
}

public class StoryCharacterGenerationService : IStoryCharacterGenerationService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly ICharacterImageProvider _images;
    private readonly IStudioMeteringService _metering;
    private readonly ITenantDocumentStore _store;

    public StoryCharacterGenerationService(
        IApplicationDbContext context, ITenantConfigOverrideProvider config, ICharacterImageProvider images, IStudioMeteringService metering, ITenantDocumentStore store)
    {
        _context = context;
        _config = config;
        _images = images;
        _metering = metering;
        _store = store;
    }

    public async Task<IReadOnlyList<GeneratedCharacterDto>> GenerateForScriptAsync(
        Guid tenantId, Guid? userId, Guid projectId, GenerateCharactersRequest request, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Story project", projectId);
        if (project.Status != StoryProjectStatus.Draft) throw new ConflictException("Characters can only be added while the script is still a draft.");
        if (project.CurrentScriptVersionId is not { } versionId) throw new ConflictException("Write the script first: the characters come from it.");

        var version = await _context.StoryScriptVersions.IgnoreQueryFilters().FirstAsync(v => v.Id == versionId, ct);
        var script = CinematicScriptValidator.Validate(version.Json, project.TargetSeconds).Script ?? throw new ConflictException("The script is not valid.");

        var castIds = await _context.StoryProjectCharacters.IgnoreQueryFilters().Where(l => l.ProjectId == projectId).Select(l => l.CharacterId).ToListAsync(ct);
        var castNames = await _context.StoryCharacters.IgnoreQueryFilters().Where(c => castIds.Contains(c.Id)).Select(c => c.Name).ToListAsync(ct);
        var missing = script.Characters.Where(c => !castNames.Contains(c.Name.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0) return Array.Empty<GeneratedCharacterDto>();

        await EnsureConfirmedAsync(missing.Count, request.ConfirmCost);

        var made = new List<GeneratedCharacterDto>();
        foreach (var wanted in missing)
        {
            var character = new StoryCharacter
            {
                TenantId = tenantId, Name = wanted.Name.Trim(), VisualAttributes = wanted.VisualDescription, VoicePreferences = wanted.VoiceInstructions,
                Origin = StoryCharacterOrigin.ScriptGenerated, CreatedBy = userId
            };
            _context.StoryCharacters.Add(character);
            _context.StoryProjectCharacters.Add(new StoryProjectCharacter { TenantId = tenantId, ProjectId = projectId, CharacterId = character.Id });
            await _context.SaveChangesAsync(ct);

            var preview = await MakePreviewAsync(character, project, userId, wanted.VisualDescription, null, ct);
            made.Add(new GeneratedCharacterDto(ToDto(character, tenantId), preview));
        }

        return made;
    }

    public async Task<StoryReferenceDto> GenerateReferenceAsync(Guid tenantId, Guid? userId, Guid characterId, GenerateReferenceRequest request, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var character = await _context.StoryCharacters.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == characterId && c.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Character", characterId);
        await EnsureConfirmedAsync(1, request.ConfirmCost);

        var link = await _context.StoryProjectCharacters.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.CharacterId == characterId, ct);
        var project = link is null ? null : await _context.StoryProjects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == link.ProjectId, ct);
        return await MakePreviewAsync(character, project, userId, character.VisualAttributes ?? character.Description ?? character.Name, request.Instruction, ct);
    }

    private Task EnsureConfirmedAsync(int pictures, bool confirmed)
    {
        var each = _images.EstimateCostUsd() ?? 0m;
        if (each > 0 && !confirmed)
            throw new ArgumentException($"This will make {pictures} picture(s) costing about {each * pictures:0.00} USD. Confirm the cost to continue.");
        return Task.CompletedTask;
    }

    private async Task<StoryReferenceDto> MakePreviewAsync(
        StoryCharacter character, StoryProject? project, Guid? userId, string look, string? instruction, CancellationToken ct)
    {
        if (!_store.IsConfigured) throw new StorageUnavailableException("File storage is not set up yet, so pictures cannot be saved.");

        var prompt = $"Full-body character reference sheet, neutral pose, plain background, consistent face. {character.Name}: {look}." +
                     (string.IsNullOrWhiteSpace(instruction) ? string.Empty : $" Change: {instruction.Trim()}.");
        var estimate = _images.EstimateCostUsd() ?? 0m;
        Guid? activity = null;
        if (estimate > 0)
        {
            var charge = await _metering.ChargeAsync(new StudioChargeRequest(
                character.TenantId, StudioActivityKind.StoryCharacterImage, $"char-image:{character.Id:N}:{Guid.NewGuid():N}", $"Picture of {character.Name}",
                ReferenceType: nameof(StoryCharacter), ReferenceId: character.Id.ToString(), Provider: _images.Name, ActorUserId: userId), ct);
            if (!charge.Allowed) throw new PlanLimitExceededException(charge.Message ?? "Your content quota is used up.");
            activity = charge.ActivityId;
        }

        var result = await _images.GenerateAsync(prompt, ct);
        var job = new StoryGenerationJob
        {
            TenantId = character.TenantId, ProjectId = project?.Id ?? Guid.Empty, Kind = StoryJobKind.CharacterImage, Provider = _images.Name,
            IdempotencyKey = $"char-image:{character.Id:N}:{Guid.NewGuid():N}", EstimatedCostUsd = estimate, ChargeActivityId = activity,
            Status = result.Success ? StoryJobStatus.Completed : StoryJobStatus.Failed, Error = result.Error, ActualCostUsd = result.CostUsd ?? (result.Success ? estimate : null)
        };
        if (project is not null) _context.StoryGenerationJobs.Add(job);

        if (!result.Success || result.Png is null)
        {
            if (activity is { } a) await _metering.RefundAsync(a, result.Error ?? "The picture could not be made.", ct);
            await _context.SaveChangesAsync(ct);
            throw new InvalidOperationException(result.Error ?? "The picture could not be made. Nothing was charged.");
        }

        var existing = await _context.StoryCharacterReferences.IgnoreQueryFilters().Where(r => r.CharacterId == character.Id).ToListAsync(ct);
        var version = existing.Count == 0 ? 1 : existing.Max(r => r.Version) + 1;
        using var stream = new MemoryStream(result.Png);
        var key = await _store.SaveAsync(character.TenantId, $"stories/characters/{character.Id}/v{version}.png", stream, "image/png", ct);

        var reference = new StoryCharacterReference
        {
            TenantId = character.TenantId, CharacterId = character.Id, StorageKey = key, ContentType = "image/png", SizeBytes = result.Png.Length,
            Checksum = Convert.ToHexString(SHA256.HashData(result.Png)), Status = StoryReferenceStatus.Generated, Version = version, GenerationPrompt = prompt
        };
        _context.StoryCharacterReferences.Add(reference);
        if (activity is { } done) await _metering.CompleteAsync(done, ct);
        await _context.SaveChangesAsync(ct);
        return new StoryReferenceDto(reference.Id, reference.Version, reference.Status, reference.ContentType, reference.SizeBytes, null, null);
    }

    private static StoryCharacterDto ToDto(StoryCharacter c, Guid tenantId) => new(
        c.Id, c.Name, c.Description, c.Personality, c.VisualAttributes, c.VoicePreferences, c.Origin, c.Visibility, c.TenantId == tenantId, c.RightsConfirmed, null, 0);
}
