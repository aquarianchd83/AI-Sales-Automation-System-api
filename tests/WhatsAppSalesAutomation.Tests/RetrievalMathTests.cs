using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The pure parts of retrieval: query preparation, fusion, boosting and the evidence gate.
/// The gate tests are the ones that matter - it is the component whose whole job is to say "no".</summary>
public class RetrievalMathTests
{
    private static readonly ITokenCounter Tokens = new HeuristicTokenCounter();
    private static readonly SupportRagOptions Options = new();

    // ── Query normalization ──────────────────────────────────────────────────────────────

    [Fact]
    public void Emails_phones_and_ids_are_masked()
    {
        var q = QueryNormalizer.Normalize(
            "My email is a.b@example.com, call +91 98765 43210, tenant 3f2504e0-4f89-11d3-9a0c-0305e82c3301 has a problem", Tokens);

        Assert.Contains("<EMAIL>", q);
        Assert.Contains("<PHONE>", q);
        Assert.Contains("<ID>", q);
        Assert.DoesNotContain("example.com", q);
        Assert.DoesNotContain("98765", q);
    }

    [Fact]
    public void Error_codes_and_versions_are_not_mistaken_for_phone_numbers()
    {
        // The digit-count check exists for exactly this: 131047 is a WhatsApp error, and it is the
        // most valuable token in the query.
        var q = QueryNormalizer.Normalize("Getting error 131047 on version 2.10.0 since yesterday", Tokens);

        Assert.Contains("131047", q);
        Assert.Contains("2.10.0", q);
        Assert.DoesNotContain("<PHONE>", q);
    }

    [Fact]
    public void A_quoted_reply_is_dropped()
    {
        var q = QueryNormalizer.Normalize(
            "How do I buy more credits?\n\nOn Mon, 5 Jan 2026 at 10:00, Support <s@x.com> wrote:\n> Your credits have expired.", Tokens);

        Assert.Equal("How do I buy more credits?", q);
    }

    [Fact]
    public void Quoted_lines_are_dropped_even_without_a_marker()
    {
        Assert.DoesNotContain("earlier text", QueryNormalizer.Normalize("Real question here\n> earlier text", Tokens));
    }

    [Fact]
    public void An_overlong_message_is_condensed_and_keeps_both_ends()
    {
        var middle = string.Join(" ", Enumerable.Repeat("This is filler sentence number that says nothing at all.", 700));
        var raw = "Why was my template rejected? " + middle + " Please tell me how to fix the template.";

        var q = QueryNormalizer.Normalize(raw, Tokens);

        Assert.True(Tokens.Count(q) <= QueryNormalizer.MaxQueryTokens);
        Assert.StartsWith("Why was my template rejected?", q);
        Assert.EndsWith("how to fix the template.", q);
    }

    [Fact]
    public void Empty_input_normalizes_to_empty()
    {
        Assert.Equal(string.Empty, QueryNormalizer.Normalize("   ", Tokens));
        Assert.Equal(string.Empty, QueryNormalizer.Normalize(null, Tokens));
    }

    // ── Query expansion ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_verbatim_query_is_always_first_and_full_weight()
    {
        var queries = QueryExpander.Expand("credits gone", Array.Empty<string>(), null, null);

        Assert.Equal("Q1-verbatim", queries[0].Name);
        Assert.Equal(1.0, queries[0].Weight);
    }

    [Fact]
    public void A_canonical_query_is_added_when_intent_and_module_add_something()
    {
        var queries = QueryExpander.Expand("quota khatam ho gya", Array.Empty<string>(), SupportIntent.CreditBalanceQuestion, null);

        var canonical = Assert.Single(queries, q => q.Name == "Q2-canonical");
        Assert.Contains("Credit Balance Question", canonical.Text);
        Assert.Contains("Quota", canonical.Text);   // module derived from the intent
        Assert.Equal(0.8, canonical.Weight);
    }

    [Fact]
    public void No_canonical_query_when_it_would_only_repeat_the_verbatim_one()
    {
        // Two identical rankings fused would just double-count one opinion.
        Assert.Single(QueryExpander.Expand("credits gone", Array.Empty<string>(), null, null));
    }

    [Fact]
    public void Error_codes_are_lifted_into_the_canonical_query()
    {
        var canonical = QueryExpander.BuildCanonical("it fails with 131047 after upgrading to v2.4.1", SupportIntent.TechnicalError, null);

        Assert.Contains("131047", canonical);
        Assert.Contains("2.4.1", canonical);
    }

    [Fact]
    public void A_contextual_query_appears_only_with_earlier_conversation()
    {
        Assert.DoesNotContain(QueryExpander.Expand("and that?", Array.Empty<string>(), null, null), q => q.Name == "Q3-contextual");

        var withHistory = QueryExpander.Expand("and that?", new[] { "How do I buy credits?", "Use the wallet screen." }, null, null);
        var q3 = Assert.Single(withHistory, q => q.Name == "Q3-contextual");

        Assert.Contains("buy credits", q3.Text);
        Assert.Equal(0.6, q3.Weight);
    }

    [Fact]
    public void An_unknown_intent_contributes_nothing_to_the_canonical_query()
    {
        Assert.Null(QueryExpander.BuildCanonical("hello", SupportIntent.Unknown, null));
    }

    // ── Fusion ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_chunk_found_by_both_legs_beats_one_found_by_a_single_leg()
    {
        var both = Guid.NewGuid();
        var vectorOnly = Guid.NewGuid();
        var keywordOnly = Guid.NewGuid();

        var fused = RankFusion.Fuse(new[]
        {
            new RankedList("vec", 0.55, new[] { vectorOnly, both }),
            new RankedList("kw", 0.45, new[] { keywordOnly, both })
        }, k: 60);

        // Rank 2 in each list, yet ahead of two rank-1 singletons: agreement between independent
        // legs is the strongest signal fusion has.
        Assert.Equal(both, fused[0].ChunkId);
    }

    [Fact]
    public void Fusion_uses_rank_not_score_and_is_deterministic()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var lists = new[] { new RankedList("v", 1.0, new[] { a, b }) };

        var first = RankFusion.Fuse(lists, 60);

        Assert.Equal(1.0 / 61, first[0].Score, 10);
        Assert.Equal(1.0 / 62, first[1].Score, 10);
        Assert.Equal(first.Select(x => x.ChunkId), RankFusion.Fuse(lists, 60).Select(x => x.ChunkId));
    }

    [Fact]
    public void The_maximum_possible_score_is_rank_one_in_every_list()
    {
        var lists = new[]
        {
            new RankedList("v", 0.55, new[] { Guid.NewGuid() }),
            new RankedList("k", 0.45, new[] { Guid.NewGuid() }),
            new RankedList("empty", 9.0, Array.Empty<Guid>())   // a leg that returned nothing adds no ceiling
        };

        Assert.Equal((0.55 + 0.45) / 61, RankFusion.MaxPossible(lists, 60), 10);
    }

    // ── Boosting ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 1.35)]
    [InlineData(90, 1.35)]
    [InlineData(80, 1.20)]
    [InlineData(70, 1.20)]
    [InlineData(60, 1.05)]
    [InlineData(40, 1.05)]
    [InlineData(30, 1.00)]
    [InlineData(10, 1.00)]
    public void Authority_multipliers_follow_the_table(int rank, double expected) =>
        Assert.Equal(expected, MetadataBooster.Authority(rank));

    [Fact]
    public void Module_boost_rewards_a_match_and_penalizes_a_mismatch_but_not_a_cross_cutting_article()
    {
        Assert.Equal(1.25, MetadataBooster.Module(ProductModule.Billing, ProductModule.Billing));
        Assert.Equal(0.80, MetadataBooster.Module(ProductModule.Campaigns, ProductModule.Billing));
        Assert.Equal(1.00, MetadataBooster.Module(null, ProductModule.Billing));
        Assert.Equal(1.00, MetadataBooster.Module(ProductModule.Billing, null));
    }

    [Fact]
    public void Recency_prefers_fresh_content_and_never_zeroes_old_content()
    {
        var now = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(1.10, MetadataBooster.Recency(now.AddDays(-10), now));
        Assert.Equal(1.00, MetadataBooster.Recency(now.AddDays(-200), now));
        Assert.Equal(0.95, MetadataBooster.Recency(now.AddDays(-400), now));
        Assert.Equal(1.00, MetadataBooster.Recency(null, now));
    }

    [Fact]
    public void No_multiplier_can_reach_zero()
    {
        // A boost small enough to act as a filter would make "why did this not come back" unanswerable.
        var now = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
        var worst = MetadataBooster.Multiplier(0, ProductModule.Campaigns, ProductModule.Billing, now.AddDays(-999), now);

        Assert.True(worst > 0.7);
    }

    [Fact]
    public void The_final_score_is_three_quarters_rerank_and_one_quarter_prerank()
    {
        Assert.Equal(0.75 * 0.8 + 0.25 * 0.4, MetadataBooster.Final(0.8, 0.4), 10);
    }

    // ── The evidence gate ────────────────────────────────────────────────────────────────

    private static EvidenceGateResult Gate(RetrievalMode mode, params (double Score, int Authority)[] chunks) =>
        EvidenceGate.Evaluate(chunks.OrderByDescending(c => c.Score).ToList(), Options, mode);

    [Fact]
    public void No_evidence_fails_the_gate()
    {
        var result = Gate(RetrievalMode.Reranked);

        Assert.False(result.Passed);
        Assert.Equal("NoEvidence", result.FailureReason);
    }

    [Fact]
    public void A_strong_top_chunk_with_corroboration_passes()
    {
        var result = Gate(RetrievalMode.Reranked, (0.85, 60), (0.70, 60), (0.30, 60));

        Assert.True(result.Passed);
        Assert.Equal(2, result.SupportingCount);
    }

    [Fact]
    public void A_top_score_below_the_threshold_fails_however_many_weak_chunks_agree()
    {
        var result = Gate(RetrievalMode.Reranked, (0.55, 60), (0.54, 60), (0.53, 60), (0.52, 60));

        Assert.False(result.Passed);
        Assert.Equal("TopScoreBelowThreshold", result.FailureReason);
    }

    [Fact]
    public void One_chunk_is_not_corroboration()
    {
        // 0.70 is a decent score but only a single piece of evidence, and the single-chunk exception
        // needs 0.80 AND authority 70.
        var result = Gate(RetrievalMode.Reranked, (0.70, 60), (0.20, 60));

        Assert.False(result.Passed);
        Assert.Equal("InsufficientSupportingEvidence", result.FailureReason);
    }

    [Fact]
    public void The_single_chunk_exception_needs_both_a_high_score_and_high_authority()
    {
        Assert.True(Gate(RetrievalMode.Reranked, (0.85, 70), (0.20, 70)).Passed);
        Assert.False(Gate(RetrievalMode.Reranked, (0.85, 30), (0.20, 30)).Passed);   // tenant content cannot carry it alone
        Assert.False(Gate(RetrievalMode.Reranked, (0.75, 90), (0.20, 90)).Passed);   // strong source, not strong enough match
    }

    [Fact]
    public void Two_close_results_from_different_authority_tiers_are_ambiguous()
    {
        // A policy and a tenant note scoring within 0.05 may say different things. Picking one is a
        // coin flip presented as an answer.
        var result = Gate(RetrievalMode.Reranked, (0.82, 90), (0.80, 30), (0.79, 30));

        Assert.False(result.Passed);
        Assert.Equal("AmbiguousAcrossAuthorityTiers", result.FailureReason);
    }

    [Fact]
    public void Two_close_results_from_the_same_tier_are_not_ambiguous()
    {
        Assert.True(Gate(RetrievalMode.Reranked, (0.82, 60), (0.80, 60), (0.79, 60)).Passed);
    }

    [Fact]
    public void Without_a_reranker_the_gate_is_stricter()
    {
        // The same evidence that passes with a reranker must fail without one: nothing has checked
        // that these chunks ANSWER the question. The agent escalates more; it does not guess more.
        var chunks = new[] { (0.70, 90), (0.60, 90) };

        Assert.True(Gate(RetrievalMode.Reranked, chunks).Passed);
        Assert.False(Gate(RetrievalMode.FusionOnly, chunks).Passed);
    }

    [Fact]
    public void Fusion_only_disables_the_single_chunk_exception_and_needs_three_supporting_chunks()
    {
        Assert.False(Gate(RetrievalMode.FusionOnly, (0.95, 100), (0.10, 100)).Passed);

        Assert.True(Gate(RetrievalMode.FusionOnly, (0.90, 60), (0.60, 60), (0.55, 60)).Passed);
    }

    // ── Options: the safety floors ───────────────────────────────────────────────────────

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.Empty(new SupportRagOptions().Validate());
    }

    [Fact]
    public void A_top_score_threshold_below_the_floor_is_rejected()
    {
        var problems = new SupportRagOptions { MinTopRerankScore = 0.49 }.Validate();

        Assert.Contains(problems, p => p.Contains("MinTopRerankScore") && p.Contains("safety floor"));
    }

    [Fact]
    public void Zero_supporting_chunks_is_rejected()
    {
        Assert.Contains(new SupportRagOptions { MinSupportingChunks = 0 }.Validate(), p => p.Contains("MinSupportingChunks"));
    }

    [Fact]
    public void A_supporting_threshold_above_the_top_threshold_is_rejected_because_the_gate_could_never_pass()
    {
        var problems = new SupportRagOptions { MinTopRerankScore = 0.6, MinSupportingRerankScore = 0.7 }.Validate();

        Assert.Contains(problems, p => p.Contains("never pass"));
    }

    [Fact]
    public void The_single_chunk_exception_cannot_be_easier_than_the_ordinary_gate()
    {
        Assert.Contains(new SupportRagOptions { SingleChunkMinScore = 0.55 }.Validate(), p => p.Contains("SingleChunkMinScore"));
    }

    [Fact]
    public void A_pipeline_that_cannot_feed_itself_is_rejected()
    {
        Assert.Contains(new SupportRagOptions { FusionTopN = 500 }.Validate(), p => p.Contains("FusionTopN"));
        Assert.Contains(new SupportRagOptions { RerankTopN = 99 }.Validate(), p => p.Contains("RerankTopN"));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var problems = new SupportRagOptions { MinTopRerankScore = 0.1, MinSupportingChunks = 0, RrfK = 0 }.Validate();

        Assert.True(problems.Count >= 3);
    }

    [Theory]
    [InlineData("en", 0.55)]
    [InlineData("en-IN", 0.55)]
    [InlineData(null, 0.55)]
    [InlineData("hi", 0.70)]
    public void Hindi_queries_lean_on_the_vector_leg(string? language, double expectedVectorWeight)
    {
        Assert.Equal(expectedVectorWeight, new SupportRagOptions().FusionWeightsFor(language).Vector);
    }

    // ── Intents ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SupportIntent.HumanAgentRequest)]
    [InlineData(SupportIntent.SecurityPrivacyConcern)]
    [InlineData(SupportIntent.LegalComplianceQuery)]
    [InlineData(SupportIntent.Unknown)]
    public void Prohibited_intents_are_never_answerable(SupportIntent intent) =>
        Assert.Equal(SupportRiskLevel.Prohibited, SupportIntentRules.RiskOf(intent));

    [Fact]
    public void Every_intent_has_a_risk_level()
    {
        // A new enum member added without a rule falls into Prohibited by the default arm, which is
        // the safe direction - this pins that the defined ones are deliberate rather than accidental.
        foreach (var intent in Enum.GetValues<SupportIntent>())
            Assert.True(Enum.IsDefined(SupportIntentRules.RiskOf(intent)));

        Assert.Equal(SupportRiskLevel.High, SupportIntentRules.RiskOf(SupportIntent.RefundRequest));
        Assert.Equal(SupportRiskLevel.Low, SupportIntentRules.RiskOf(SupportIntent.HowToUseFeature));
    }
}
