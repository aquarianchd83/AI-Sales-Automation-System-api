using System.Text;
using System.Text.Json;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

/// <summary>One verified fact about the tenant's business, as the story studio shows it and records it.</summary>
public sealed record StoryKnowledgeFactDto(Guid Id, string Category, string? Subject, string Statement);

/// <summary>What the story studio knows about the tenant's business, from the knowledge they already gave (verified Business Facts only).</summary>
public sealed record StoryKnowledgeDto(bool IsReady, int VerifiedFacts, IReadOnlyList<StoryKnowledgeFactDto> Facts, string? Message);

/// <summary>A video idea: one real customer problem the business solves, and how, with the facts it rests on.</summary>
public sealed record StoryIdeaDto(string Title, string Problem, string Solution, IReadOnlyList<Guid> FactIds);

/// <summary>One spoken line checked against the verified facts. A risky claim (a percentage, a guarantee, "best") that no fact supports must be changed before approval.</summary>
public sealed record ScriptClaimDto(int Scene, int Order, string Speaker, string Text, bool Supported, bool IsRisky, string Reason);

public sealed record SuggestIdeasRequest(string? Language);

public interface IStoryGroundingService
{
    Task<StoryKnowledgeDto> GetKnowledgeAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>The verified facts a script on this topic may rest on: the ones that match it, topped up with the rest so the model has enough to work from.
    /// Throws a sentence when the tenant has no verified facts at all: nothing is written from nothing.</summary>
    Task<GroundingResultDto> GatherAsync(Guid tenantId, string topic, CancellationToken ct = default);

    Task<IReadOnlyList<StoryIdeaDto>> SuggestIdeasAsync(Guid tenantId, Guid? userId, string? language, CancellationToken ct = default);

    Task<IReadOnlyList<ScriptClaimDto>> CheckScriptAsync(Guid tenantId, CinematicScript script, CancellationToken ct = default);
}

public class StoryGroundingService : IStoryGroundingService
{
    public const int IdeaCount = 4;
    public const int MaxFactsForIdeas = 30;

    public const string NoFactsMessage =
        "You have no verified business facts yet, so the studio has nothing true to write about. Answer the Business Questionnaire and verify your facts first.";

    private readonly IKnowledgeRetrievalService _retrieval;
    private readonly IKnowledgeService _knowledge;
    private readonly IAiTaskRunner _ai;
    private readonly IStudioMeteringService _metering;

    public StoryGroundingService(IKnowledgeRetrievalService retrieval, IKnowledgeService knowledge, IAiTaskRunner ai, IStudioMeteringService metering)
    {
        _retrieval = retrieval;
        _knowledge = knowledge;
        _ai = ai;
        _metering = metering;
    }

    public async Task<StoryKnowledgeDto> GetKnowledgeAsync(Guid tenantId, CancellationToken ct = default)
    {
        var all = await _retrieval.RetrieveAsync(tenantId, null, 50, ct);
        var facts = all.Facts.Select(f => new StoryKnowledgeFactDto(f.Id, f.Category, f.Subject, f.Statement)).ToList();
        return new StoryKnowledgeDto(facts.Count > 0, facts.Count, facts, facts.Count > 0 ? null : NoFactsMessage);
    }

    public async Task<GroundingResultDto> GatherAsync(Guid tenantId, string topic, CancellationToken ct = default)
    {
        var matching = await _retrieval.RetrieveAsync(tenantId, topic, 20, ct);
        var facts = matching.Facts.ToList();
        if (facts.Count < 6)
        {
            // A problem statement rarely shares words with every useful fact (who it is for, what it costs, how it works), so top up with the rest.
            var rest = await _retrieval.RetrieveAsync(tenantId, null, 20, ct);
            facts.AddRange(rest.Facts.Where(f => facts.All(x => x.Id != f.Id)).Take(20 - facts.Count));
        }

        if (facts.Count == 0) throw new ConflictException(NoFactsMessage);
        return new GroundingResultDto(facts, matching.Facts.Count == facts.Count ? matching.PromptBlock : (await _retrieval.RetrieveAsync(tenantId, null, facts.Count, ct)).PromptBlock);
    }

    public async Task<IReadOnlyList<StoryIdeaDto>> SuggestIdeasAsync(Guid tenantId, Guid? userId, string? language, CancellationToken ct = default)
    {
        var all = await _retrieval.RetrieveAsync(tenantId, null, MaxFactsForIdeas, ct);
        if (all.Facts.Count == 0) throw new ConflictException(NoFactsMessage);

        var charge = await _metering.ChargeAsync(new StudioChargeRequest(
            tenantId, StudioActivityKind.StoryScript, $"story-ideas:{tenantId:N}:{Guid.NewGuid():N}", "Story video ideas",
            ReferenceType: nameof(StoryKnowledgeDto), ActorUserId: userId), ct);
        if (!charge.Allowed) throw new PlanLimitExceededException(charge.Message ?? "Your content generation quota is used up.");

        try
        {
            var result = await _ai.RunAsync(new AiTaskRequest(
                AiTaskKinds.CinematicIdeas, StoryPrompts.IdeasSystem, StoryPrompts.IdeasUser(all.Facts, language, IdeaCount), 1800, ExpectJson: true, TenantId: tenantId), ct);

            if (result.IsSimulated)
            {
                await _metering.RefundAsync(charge.ActivityId, "Test data: no AI provider was used.", ct);
                return SimulatedIdeas(all.Facts);
            }

            if (!result.Success)
            {
                await _metering.RefundAsync(charge.ActivityId, result.ErrorMessage ?? "The AI provider failed.", ct);
                throw new InvalidOperationException(result.ErrorMessage ?? "The AI provider could not suggest ideas. Nothing was charged.");
            }

            var ideas = ParseIdeas(result.Text, all.Facts.Select(f => f.Id).ToHashSet());
            if (ideas.Count == 0)
            {
                await _metering.RefundAsync(charge.ActivityId, "The AI reply held no usable ideas.", ct);
                throw new InvalidOperationException("The AI did not return usable ideas, so nothing was charged. Try again.");
            }

            await _metering.CompleteAsync(charge.ActivityId, ct);
            return ideas;
        }
        catch (OperationCanceledException)
        {
            await _metering.RefundAsync(charge.ActivityId, "Cancelled.", CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<ScriptClaimDto>> CheckScriptAsync(Guid tenantId, CinematicScript script, CancellationToken ct = default)
    {
        var lines = new List<(int Scene, int Order, string Speaker, string Text)>();
        foreach (var scene in script.Scenes)
        {
            var order = 0;
            foreach (var d in (scene.Dialogue ?? Array.Empty<CinematicDialogue>()).OrderBy(x => x.StartMs))
            {
                order++;
                if (!string.IsNullOrWhiteSpace(d.Text)) lines.Add((scene.Index, order, d.Speaker, d.Text.Trim()));
            }
        }

        if (lines.Count == 0 || lines.Count > KnowledgeService.MaxClaims) return Array.Empty<ScriptClaimDto>();

        var checkedLines = await _knowledge.CheckClaimsAsync(tenantId, new CheckClaimsRequest(lines.Select(l => l.Text.Length > KnowledgeService.MaxClaimLength ? l.Text[..KnowledgeService.MaxClaimLength] : l.Text).ToList()), ct);
        return lines.Zip(checkedLines.Claims, (line, claim) => new ScriptClaimDto(
            line.Scene, line.Order, line.Speaker, line.Text, claim.Verdict == ClaimVerdict.Supported, claim.IsRisky && claim.Verdict != ClaimVerdict.Supported, claim.Reason)).ToList();
    }

    private static IReadOnlyList<StoryIdeaDto> ParseIdeas(string? json, HashSet<Guid> known)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<StoryIdeaDto>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ideas", out var list) || list.ValueKind != JsonValueKind.Array) return Array.Empty<StoryIdeaDto>();

            var ideas = new List<StoryIdeaDto>();
            foreach (var item in list.EnumerateArray().Take(IdeaCount + 2))
            {
                var title = Text(item, "title");
                var problem = Text(item, "problem");
                var solution = Text(item, "solution");
                if (title is null || problem is null || solution is null) continue;

                // An idea must rest on facts the model was actually given: an id it made up means it invented the idea.
                var ids = item.TryGetProperty("factIds", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(e => Guid.TryParse(e.GetString(), out var g) ? g : Guid.Empty).Where(known.Contains).Distinct().ToList()
                    : new List<Guid>();
                if (ids.Count == 0) continue;

                ideas.Add(new StoryIdeaDto(Clip(title, 120), Clip(problem, 400), Clip(solution, 400), ids));
            }

            return ideas.Take(IdeaCount).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<StoryIdeaDto>();
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()) ? e.GetString()!.Trim() : null;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    private static IReadOnlyList<StoryIdeaDto> SimulatedIdeas(IReadOnlyList<GroundingFactDto> facts) =>
        facts.Take(3).Select((f, i) => new StoryIdeaDto(
            $"[Test data] Idea {i + 1}: {(f.Subject ?? f.Category)}",
            $"[Test data] A customer problem connected to: {f.Statement}",
            $"[Test data] How the business helps, using only: {f.Statement}",
            new[] { f.Id })).ToList();
}

public static class StoryIdeaPrompts
{
    public static string FactLines(IReadOnlyList<GroundingFactDto> facts)
    {
        var sb = new StringBuilder();
        foreach (var f in facts) sb.Append('[').Append(f.Id).Append("] ").Append(f.Category).Append(f.Subject is null ? "" : " / " + f.Subject).Append(": ").AppendLine(f.Statement);
        return sb.ToString();
    }
}
