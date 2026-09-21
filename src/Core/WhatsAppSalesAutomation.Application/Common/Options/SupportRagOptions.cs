namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Retrieval and answering thresholds for the AI Support Agent, bound from the "SupportRag" config
/// section.
///
/// Separate from <see cref="AiOptions"/> on purpose: AiOptions governs the SALES orchestrator
/// (customer-facing WhatsApp replies), and the two have genuinely different risk profiles. A
/// slightly-off sales reply costs a lead; a slightly-off support reply about a refund policy costs
/// trust and possibly money. Sharing one set of thresholds would mean tuning one of them wrong.
///
/// Per-tenant overridable through the existing <c>ITenantConfigOverrideProvider</c> path, but not
/// without limit - see <see cref="Validate"/>, which is a safety floor rather than a tuning knob.
/// </summary>
public class SupportRagOptions
{
    // ── Candidate generation ──────────────────────────────────────────────────

    /// <summary>Chunks the vector leg returns before fusion.</summary>
    public int VectorTopN { get; set; } = 50;

    /// <summary>Chunks the keyword leg returns before fusion. Zero in effect when full-text search is
    /// unavailable - see <c>IKeywordSearchStore</c>, which degrades to vector-only per EC-23.</summary>
    public int KeywordTopN { get; set; } = 50;

    /// <summary>Chunks surviving RRF fusion and metadata boosting, i.e. what the reranker is asked to
    /// score. The reranker is the expensive stage, so this is the real cost lever.</summary>
    public int FusionTopN { get; set; } = 30;

    /// <summary>Chunks kept after reranking, before atomic-group expansion. Expansion can push the
    /// final count above this - a rule that spans three chunks arrives whole or not at all (§M.3).</summary>
    public int RerankTopN { get; set; } = 8;

    /// <summary>Reciprocal Rank Fusion's damping constant. 60 is the value from the original RRF
    /// paper and is deliberately not tuned here: it flattens the difference between rank 1 and rank 2
    /// enough that neither leg can dominate on its own ordering alone.</summary>
    public int RrfK { get; set; } = 60;

    // ── Fusion weights ────────────────────────────────────────────────────────

    public double VectorWeight { get; set; } = 0.55;

    public double KeywordWeight { get; set; } = 0.45;

    /// <summary>Weights for a query detected as non-English. SQL Server's Hindi word breaker is weak
    /// (§J.6), so the keyword leg contributes less reliably there, while the embedding model is
    /// genuinely multilingual - the fusion leans toward the leg that still works.</summary>
    public double VectorWeightNonEnglish { get; set; } = 0.70;

    public double KeywordWeightNonEnglish { get; set; } = 0.30;

    // ── Evidence gate (§S.2) ──────────────────────────────────────────────────

    /// <summary>Rerank score the single best chunk must reach before an autonomous answer is even
    /// considered. Below this the agent clarifies or escalates - it never answers "as best it can",
    /// which is the failure mode that makes a support bot worse than no support bot.</summary>
    public double MinTopRerankScore { get; set; } = 0.62;

    /// <summary>A chunk below this is not passed to the model at all. An irrelevant "closest
    /// available" chunk is worse than no chunk: it invites the model to stretch.</summary>
    public double MinSupportingRerankScore { get; set; } = 0.50;

    /// <summary>How many chunks must clear <see cref="MinSupportingRerankScore"/>. Two independent
    /// pieces of evidence, not one - a single high score is evidence that retrieval succeeded, not
    /// that the question is documented. Counts chunks, not distinct articles (EC-28): one good
    /// article's Rule and Exceptions sections are two valid independent pieces of evidence.</summary>
    public int MinSupportingChunks { get; set; } = 2;

    /// <summary>A single chunk may carry an answer alone only if it scores at least this AND comes
    /// from a source of at least <see cref="SingleChunkMinAuthority"/>. Set high deliberately: this is
    /// the "one crisp FAQ answers it exactly" case, not a general fallback for thin evidence.</summary>
    public double SingleChunkMinScore { get; set; } = 0.80;

    public int SingleChunkMinAuthority { get; set; } = 70;

    /// <summary>Gap between the best and second-best chunk below which, when the two disagree on
    /// authority tier, the agent treats the result as ambiguous rather than picking a winner.</summary>
    public double AmbiguityGapThreshold { get; set; } = 0.05;

    // ── Intent ────────────────────────────────────────────────────────────────

    public double MinIntentConfidence { get; set; } = 0.55;

    // ── Context assembly ──────────────────────────────────────────────────────

    public int MaxContextTokens { get; set; } = 6000;

    public int ConversationHistoryTurns { get; set; } = 8;

    // ── Tools ─────────────────────────────────────────────────────────────────

    /// <summary>More than four tool calls means the question is a complex investigation, which is a
    /// human's job - so the cap doubles as an escalation signal rather than only a cost control.</summary>
    public int MaxToolCallsPerRun { get; set; } = 4;

    public int ToolTimeoutSeconds { get; set; } = 8;

    // ── Loop control ──────────────────────────────────────────────────────────

    /// <summary>Consecutive AI turns on one ticket before it escalates regardless of confidence.
    /// Three failed attempts is a pattern, not bad luck.</summary>
    public int MaxAiTurnsPerTicket { get; set; } = 3;

    public int MaxClarificationRounds { get; set; } = 2;

    // ── Safety floors ─────────────────────────────────────────────────────────

    /// <summary>Below this, <see cref="MinTopRerankScore"/> is not a threshold any more - the agent
    /// would answer from whatever retrieval happened to return.</summary>
    public const double MinTopRerankScoreFloor = 0.50;

    /// <summary>One supporting chunk means no corroboration at all. The single-chunk exception above
    /// is the sanctioned way to answer from one chunk, and it carries its own much higher bar.</summary>
    public const int MinSupportingChunksFloor = 1;

    /// <summary>
    /// Validates a configured or per-tenant-overridden instance, returning one message per violation
    /// and an empty list when it is acceptable.
    ///
    /// The two floors are not tuning knobs. A tenant unhappy with escalation volume will reach for
    /// exactly these two numbers first, and setting them low turns a grounded agent into a confident
    /// guesser - which is the failure this whole design exists to prevent. Returning messages rather
    /// than throwing lets the settings API reject the write with all of the problems at once.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (MinTopRerankScore < MinTopRerankScoreFloor)
        {
            errors.Add($"SupportRag:MinTopRerankScore must be at least {MinTopRerankScoreFloor} " +
                       $"(got {MinTopRerankScore}). This is a safety floor, not a tuning knob: below it the " +
                       "agent answers from whatever retrieval returned.");
        }

        if (MinSupportingChunks < MinSupportingChunksFloor)
        {
            errors.Add($"SupportRag:MinSupportingChunks must be at least {MinSupportingChunksFloor} " +
                       $"(got {MinSupportingChunks}). Use the single-chunk exception " +
                       $"(SingleChunkMinScore/SingleChunkMinAuthority) to answer from one chunk.");
        }

        if (MinSupportingRerankScore > MinTopRerankScore)
        {
            errors.Add($"SupportRag:MinSupportingRerankScore ({MinSupportingRerankScore}) cannot exceed " +
                       $"MinTopRerankScore ({MinTopRerankScore}) - no chunk could then be both the top " +
                       "chunk and a supporting one, so the evidence gate would never pass.");
        }

        if (SingleChunkMinScore < MinTopRerankScore)
        {
            errors.Add($"SupportRag:SingleChunkMinScore ({SingleChunkMinScore}) must be at least " +
                       $"MinTopRerankScore ({MinTopRerankScore}) - the single-chunk exception is meant to be " +
                       "harder to reach than the ordinary gate, not easier.");
        }

        // Each stage must be able to feed the next. A RerankTopN above FusionTopN is not an error the
        // pipeline would report - it would just silently return fewer chunks than configured.
        if (FusionTopN > VectorTopN + KeywordTopN)
            errors.Add($"SupportRag:FusionTopN ({FusionTopN}) exceeds the candidates both legs can supply ({VectorTopN} + {KeywordTopN}).");

        if (RerankTopN > FusionTopN)
            errors.Add($"SupportRag:RerankTopN ({RerankTopN}) exceeds FusionTopN ({FusionTopN}).");

        if (VectorTopN < 1) errors.Add("SupportRag:VectorTopN must be at least 1.");
        if (FusionTopN < 1) errors.Add("SupportRag:FusionTopN must be at least 1.");
        if (RerankTopN < 1) errors.Add("SupportRag:RerankTopN must be at least 1.");
        if (RrfK < 1) errors.Add("SupportRag:RrfK must be at least 1.");
        if (KeywordTopN < 0) errors.Add("SupportRag:KeywordTopN cannot be negative (0 disables the keyword leg).");

        if (MaxContextTokens < 500) errors.Add("SupportRag:MaxContextTokens must be at least 500.");
        if (MaxAiTurnsPerTicket < 1) errors.Add("SupportRag:MaxAiTurnsPerTicket must be at least 1.");
        if (MaxToolCallsPerRun < 0) errors.Add("SupportRag:MaxToolCallsPerRun cannot be negative.");
        if (ToolTimeoutSeconds < 1) errors.Add("SupportRag:ToolTimeoutSeconds must be at least 1.");

        foreach (var (name, value) in new[]
                 {
                     (nameof(MinTopRerankScore), MinTopRerankScore),
                     (nameof(MinSupportingRerankScore), MinSupportingRerankScore),
                     (nameof(SingleChunkMinScore), SingleChunkMinScore),
                     (nameof(AmbiguityGapThreshold), AmbiguityGapThreshold),
                     (nameof(MinIntentConfidence), MinIntentConfidence),
                     (nameof(VectorWeight), VectorWeight),
                     (nameof(KeywordWeight), KeywordWeight),
                     (nameof(VectorWeightNonEnglish), VectorWeightNonEnglish),
                     (nameof(KeywordWeightNonEnglish), KeywordWeightNonEnglish)
                 })
        {
            if (value is < 0 or > 1)
                errors.Add($"SupportRag:{name} must be between 0 and 1 (got {value}).");
        }

        if (SingleChunkMinAuthority is < 0 or > 100)
            errors.Add($"SupportRag:SingleChunkMinAuthority must be between 0 and 100 (got {SingleChunkMinAuthority}).");

        return errors;
    }

    /// <summary>The fusion weights for a given query language. Returns the non-English pair for
    /// anything that is not BCP-47 "en" - see the doc comment on
    /// <see cref="VectorWeightNonEnglish"/>.</summary>
    public (double Vector, double Keyword) FusionWeightsFor(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode) ||
        languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? (VectorWeight, KeywordWeight)
            : (VectorWeightNonEnglish, KeywordWeightNonEnglish);
}
