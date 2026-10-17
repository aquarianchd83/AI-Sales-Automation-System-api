using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;

/// <summary>
/// Works out how ready a tenant's knowledge is for each kind of content, with no database in it. Readiness is not "how many questions were
/// answered": it counts facts that match what each kind of content needs, and trusts them in proportion to how well they are backed -
/// verified and current counts in full, stated-but-unverified counts a fraction, an AI inference counts for nothing, and where a rule
/// asks for proof a fact without it does not count at all. Every number it produces comes with the sentence that explains it.
/// </summary>
public static class KnowledgeReadinessEngine
{
    public sealed record Inputs(
        ReadinessRuleSet Rules,
        IReadOnlyList<BusinessFact> Facts,
        IReadOnlyList<KnowledgeGapDecision> Decisions,
        IReadOnlyDictionary<string, string> SpecialAnswers,
        DateTime UtcNow,
        string? RulesWarning);

    private sealed record Evaluation(
        RequirementDef Def,
        RequirementState State,
        KnowledgeGapReason? Reason,
        double Fulfilment,
        string Explanation,
        IReadOnlyList<Guid> FactIds,
        IReadOnlyList<Guid> ToConfirm,
        KnowledgeGapDecision? Decision);

    public static KnowledgeReadinessDto Evaluate(Inputs input)
    {
        var rules = input.Rules;
        var live = input.Facts.Where(f => f.Status != BusinessFactStatus.Retired).ToList();
        var decisions = input.Decisions.ToDictionary(d => d.RequirementKey, StringComparer.Ordinal);

        var evaluations = rules.Requirements.ToDictionary(
            r => r.Key, r => EvaluateRequirement(r, rules.DraftCredit, live, decisions.GetValueOrDefault(r.Key), input), StringComparer.Ordinal);

        var types = rules.ContentTypes.Select(t => EvaluateContentType(t, evaluations)).ToList();

        var gaps = BuildGaps(rules, evaluations, input.UtcNow);

        var counts = new KnowledgeFactCountsDto(
            live.Count(f => KnowledgeClassifier.IsUsable(f, input.UtcNow)),
            live.Count(f => f.Status == BusinessFactStatus.Draft),
            live.Count(f => KnowledgeClassifier.Classify(f, input.UtcNow) == KnowledgeClassification.Expired),
            input.Facts.Count(f => f.Status == BusinessFactStatus.Retired));

        return new KnowledgeReadinessDto(
            types.Count == 0 ? 0 : (int)Math.Round(types.Average(t => t.Score), MidpointRounding.AwayFromZero),
            types, gaps, counts, input.RulesWarning, input.UtcNow);
    }

    // ----- one requirement -------------------------------------------------------------------------------------------------------------

    private static Evaluation EvaluateRequirement(
        RequirementDef def, double draftCredit, IReadOnlyList<BusinessFact> live, KnowledgeGapDecision? decision, Inputs input)
    {
        var now = input.UtcNow;
        var matching = live.Where(f => Matches(def, f)).ToList();

        var good = new List<BusinessFact>();
        var draft = new List<BusinessFact>();
        var needsProof = new List<BusinessFact>();
        var outdated = new List<BusinessFact>();
        var expired = new List<BusinessFact>();

        foreach (var f in matching)
        {
            var kind = KnowledgeClassifier.Classify(f, now);
            if (kind == KnowledgeClassification.UnverifiedClaim)
                continue; // an inference nobody has stood behind earns nothing, and is not even offered for confirmation as if it were the tenant's word

            if (kind == KnowledgeClassification.Expired)
            {
                expired.Add(f);
                continue;
            }

            if (kind == KnowledgeClassification.TenantApproved && def.MaxAgeDays is { } maxAge && f.VerifiedAtUtc is { } verifiedAt && verifiedAt.AddDays(maxAge) <= now)
            {
                outdated.Add(f);
                continue;
            }

            if (def.RequiresEvidence && string.IsNullOrWhiteSpace(f.Evidence))
            {
                needsProof.Add(f);
                continue;
            }

            (kind == KnowledgeClassification.TenantApproved ? good : draft).Add(f);
        }

        var credits = good.Select(_ => 1.0).Concat(draft.Select(_ => draftCredit)).OrderByDescending(c => c).Take(def.MinFacts).Sum();
        var fulfilment = Math.Min(1.0, credits / def.MinFacts);
        var factIds = good.Concat(draft).Select(f => f.Id).ToList();
        var toConfirm = draft.Select(f => f.Id).ToList();

        if (decision is { Kind: KnowledgeGapDecisionKind.Skipped } && def.Skippable)
            return new Evaluation(def, RequirementState.Skipped, null, 0, "You chose to skip this - it no longer counts for or against you.", factIds, toConfirm, decision);

        if (good.Count >= def.MinFacts)
        {
            var text = good.Count == 1 ? "1 verified fact backs this." : $"{good.Count} verified facts back this.";
            return new Evaluation(def, RequirementState.Met, null, 1.0, text, factIds, toConfirm, decision);
        }

        if (good.Count + draft.Count >= def.MinFacts)
        {
            var text = $"You have told us this, but {(draft.Count == 1 ? "1 fact is" : $"{draft.Count} facts are")} still waiting for an Admin to confirm it is true.";
            return new Evaluation(def, RequirementState.NeedsConfirmation, KnowledgeGapReason.NeedsConfirmation, fulfilment, text, factIds, toConfirm, decision);
        }

        if (def.RequiresEvidence && good.Count + draft.Count + needsProof.Count >= def.MinFacts)
        {
            return new Evaluation(def, RequirementState.NeedsEvidence, KnowledgeGapReason.NeedsEvidence, fulfilment,
                "You have told us this, but not how you can prove it. For this kind of claim we need something to back it up, such as a signed form, a report or a record.",
                needsProof.Concat(good).Concat(draft).Select(f => f.Id).ToList(), toConfirm, decision);
        }

        var total = good.Count + draft.Count + outdated.Count + expired.Count;
        if (expired.Count > 0 && total >= def.MinFacts)
        {
            return new Evaluation(def, RequirementState.Expired, KnowledgeGapReason.Expired, fulfilment,
                "This was confirmed before, but its date has passed. Please check it is still true and confirm it again.",
                expired.Select(f => f.Id).ToList(), expired.Select(f => f.Id).ToList(), decision);
        }

        if (outdated.Count > 0 && total >= def.MinFacts)
        {
            return new Evaluation(def, RequirementState.Outdated, KnowledgeGapReason.Outdated, fulfilment,
                $"This was confirmed more than {def.MaxAgeDays} days ago. Please check it is still true and confirm it again.",
                outdated.Select(f => f.Id).ToList(), outdated.Select(f => f.Id).ToList(), decision);
        }

        var special = def.SourceRefs.Select(r => input.SpecialAnswers.GetValueOrDefault(r)).FirstOrDefault(s => s is not null);
        var reason = special switch
        {
            SpecialAnswer.Unknown => KnowledgeGapReason.Unknown,
            SpecialAnswer.NeedHelp => KnowledgeGapReason.NeedsHelp,
            _ => KnowledgeGapReason.Missing
        };

        var missingText = special switch
        {
            SpecialAnswer.Unknown => "You said you did not know this. That is fine - add it when you can.",
            SpecialAnswer.NeedHelp => "You asked for help with this. Add what you know and we will build on it.",
            _ => good.Count + draft.Count > 0
                ? $"{good.Count + draft.Count} of {def.MinFacts} needed facts so far."
                : "Nothing about this yet."
        };

        return new Evaluation(def, RequirementState.Missing, reason, fulfilment, missingText, factIds, toConfirm, decision);
    }

    private static bool Matches(RequirementDef def, BusinessFact fact) =>
        def.Categories.Any(c => string.Equals(c, fact.Category, StringComparison.OrdinalIgnoreCase))
        || (fact.SourceRef is not null && def.SourceRefs.Contains(fact.SourceRef, StringComparer.Ordinal));

    // ----- one kind of content ---------------------------------------------------------------------------------------------------------

    private static ContentTypeReadinessDto EvaluateContentType(ContentTypeDef type, IReadOnlyDictionary<string, Evaluation> evaluations)
    {
        var lines = new List<RequirementResultDto>();
        double earned = 0, possible = 0;
        var criticals = new List<(Evaluation Eval, RequirementRef Ref)>();

        foreach (var reference in type.Requirements)
        {
            var eval = evaluations[reference.Key];
            var skipped = eval.State == RequirementState.Skipped;
            var points = skipped ? 0 : reference.Weight * eval.Fulfilment;

            if (!skipped)
            {
                earned += points;
                possible += reference.Weight;
                if (reference.Critical)
                    criticals.Add((eval, reference));
            }

            lines.Add(new RequirementResultDto(
                reference.Key, eval.Def.Label, reference.Weight, reference.Critical,
                Math.Round(eval.Fulfilment, 2), Math.Round(points, 1), eval.State, eval.Explanation, eval.FactIds));
        }

        var score = possible <= 0 ? 0 : (int)Math.Round(100 * earned / possible, MidpointRounding.AwayFromZero);
        var unmet = criticals.Where(c => c.Eval.State != RequirementState.Met).ToList();

        ContentReadinessStatus status;
        string summary;
        if (unmet.Count == 0 && score >= type.MinScore)
        {
            status = ContentReadinessStatus.Ready;
            summary = "Ready: the facts this needs are verified.";
        }
        else if (unmet.Count > 0 && unmet.All(c => c.Eval.State == RequirementState.NeedsConfirmation))
        {
            status = ContentReadinessStatus.NeedsConfirmation;
            summary = $"Almost: you have told us everything critical, but an Admin still has to confirm {Names(unmet)}.";
        }
        else if (unmet.Count > 0)
        {
            status = ContentReadinessStatus.NotReady;
            summary = $"Not ready: still needs {Names(unmet)}.";
        }
        else
        {
            status = ContentReadinessStatus.NotReady;
            summary = $"Not ready: the score is {score} and {type.MinScore} is needed. Fill the open gaps to raise it.";
        }

        return new ContentTypeReadinessDto(type.Key, type.Label, type.Description, score, type.MinScore, status, summary, lines);
    }

    private static string Names(IEnumerable<(Evaluation Eval, RequirementRef Ref)> items) =>
        string.Join(", ", items.Select(i => $"\"{i.Eval.Def.Label}\"").Take(3)) + (items.Count() > 3 ? " and more" : string.Empty);

    // ----- the follow-up questions -----------------------------------------------------------------------------------------------------

    private static List<KnowledgeGapDto> BuildGaps(ReadinessRuleSet rules, IReadOnlyDictionary<string, Evaluation> evaluations, DateTime now)
    {
        var gaps = new List<(KnowledgeGapDto Gap, int Criticals, double Weight)>();

        foreach (var def in rules.Requirements)
        {
            var eval = evaluations[def.Key];
            if (eval.State is RequirementState.Met or RequirementState.Skipped || eval.Reason is null)
                continue;

            var users = rules.ContentTypes.Where(t => t.Requirements.Any(r => r.Key == def.Key)).ToList();
            if (users.Count == 0)
                continue;

            var deferred = eval.Decision is { Kind: KnowledgeGapDecisionKind.Deferred } d && d.DeferUntilUtc > now;
            var criticals = users.Count(t => t.Requirements.First(r => r.Key == def.Key).Critical);
            var weight = users.Sum(t => t.Requirements.First(r => r.Key == def.Key).Weight);

            gaps.Add((new KnowledgeGapDto(
                def.Key, def.Label, eval.Reason.Value, deferred ? KnowledgeGapState.Deferred : KnowledgeGapState.Open,
                eval.Decision?.DeferUntilUtc, def.Question, def.Why, def.Example, def.RequiresEvidence, def.Skippable,
                users.Select(t => t.Label).ToList(), eval.ToConfirm, eval.FactIds), criticals, weight));
        }

        return gaps.OrderBy(g => g.Gap.State).ThenByDescending(g => g.Criticals).ThenByDescending(g => g.Weight).Select(g => g.Gap).ToList();
    }
}
