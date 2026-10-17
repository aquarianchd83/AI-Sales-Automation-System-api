using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Phase 2: the readiness score, the classification of facts and the checking of claims - all pure rules, no database.</summary>
public sealed class KnowledgeReadinessEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static BusinessFact Fact(
        string category, string statement = "A statement.", BusinessFactStatus status = BusinessFactStatus.Verified, BusinessFactOrigin origin = BusinessFactOrigin.TenantProvided,
        string? evidence = null, DateTime? verifiedAt = null, DateTime? expiresAt = null, string? sourceRef = null) => new()
    {
        TenantId = Tenant,
        Category = category,
        Statement = statement,
        Status = status,
        Origin = origin,
        Evidence = evidence,
        VerifiedAtUtc = status == BusinessFactStatus.Verified ? verifiedAt ?? Now.AddDays(-1) : null,
        ExpiresAtUtc = expiresAt,
        SourceRef = sourceRef,
        SourceType = sourceRef is null ? BusinessFactSource.Manual : BusinessFactSource.Questionnaire
    };

    private static KnowledgeReadinessDto Evaluate(
        IEnumerable<BusinessFact> facts, IEnumerable<KnowledgeGapDecision>? decisions = null, IReadOnlyDictionary<string, string>? specials = null, ReadinessRuleSet? rules = null) =>
        KnowledgeReadinessEngine.Evaluate(new KnowledgeReadinessEngine.Inputs(
            rules ?? ReadinessRules.Default, facts.ToList(), (decisions ?? Array.Empty<KnowledgeGapDecision>()).ToList(),
            specials ?? new Dictionary<string, string>(), Now, null));

    private static ContentTypeReadinessDto Type(KnowledgeReadinessDto r, string key) => r.ContentTypes.Single(t => t.Key == key);

    private static RequirementResultDto Line(KnowledgeReadinessDto r, string type, string requirement) => Type(r, type).Requirements.Single(l => l.Key == requirement);

    private static IEnumerable<BusinessFact> BasicsVerified() => new[]
    {
        Fact("About", "We are an eye clinic in Pune."), Fact("Audience", "Working adults in Pune."), Fact("Services", "Cataract surgery and eye tests."),
        Fact("Language", "English and Hindi."), Fact("Brand", "Warm and reassuring.")
    };

    // ----- the built-in rules ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_built_in_rules_are_valid()
    {
        Assert.Empty(ReadinessRules.Validate(ReadinessRules.Default));
        Assert.Equal(5, ReadinessRules.Default.ContentTypes.Count);
    }

    [Fact]
    public void A_tenant_that_has_told_us_nothing_scores_zero_and_every_kind_of_content_is_not_ready()
    {
        var result = Evaluate(Array.Empty<BusinessFact>());

        Assert.Equal(0, result.OverallScore);
        Assert.All(result.ContentTypes, t => { Assert.Equal(0, t.Score); Assert.Equal(ContentReadinessStatus.NotReady, t.Status); });
        Assert.NotEmpty(result.Gaps);
    }

    [Fact]
    public void Verified_basics_make_general_educational_content_ready_but_not_the_stricter_kinds()
    {
        var result = Evaluate(BasicsVerified());

        Assert.Equal(ContentReadinessStatus.Ready, Type(result, "general_education").Status);
        Assert.Equal(100, Type(result, "general_education").Score);
        Assert.Equal(ContentReadinessStatus.NotReady, Type(result, "offers_pricing").Status);
        Assert.Equal(ContentReadinessStatus.NotReady, Type(result, "case_studies").Status);
    }

    // ----- readiness is not "how much was answered" -----------------------------------------------------------------------------------

    [Fact]
    public void Complete_contact_information_alone_gives_no_readiness_for_a_case_study()
    {
        var facts = new[] { Fact("Contact", "Call 020 1234 5678."), Fact("Contact", "Open 9 to 6."), Fact("Contact", "12 MG Road, Pune."), Fact("About", "An eye clinic.") };

        var result = Evaluate(facts);

        Assert.Equal(ContentReadinessStatus.NotReady, Type(result, "case_studies").Status);
        Assert.True(Type(result, "case_studies").Score < 20);
        Assert.Equal(RequirementState.Missing, Line(result, "case_studies", "customer_results").State);
    }

    [Fact]
    public void Many_unverified_facts_do_not_add_up_to_readiness()
    {
        var facts = Enumerable.Range(0, 30).Select(i => Fact("Services", $"Service {i}.", BusinessFactStatus.Draft)).ToList();

        var result = Evaluate(facts);

        Assert.NotEqual(ContentReadinessStatus.Ready, Type(result, "general_education").Status);
        Assert.Equal(RequirementState.NeedsConfirmation, Line(result, "general_education", "services").State);
    }

    [Fact]
    public void A_stated_but_unverified_fact_earns_only_the_draft_credit_and_a_verified_one_earns_all_of_it()
    {
        var draft = Evaluate(new[] { Fact("About", "An eye clinic.", BusinessFactStatus.Draft) });
        var verified = Evaluate(new[] { Fact("About", "An eye clinic.") });

        Assert.Equal(0.4, Line(draft, "general_education", "identity").Fulfilment);
        Assert.Equal(1.0, Line(verified, "general_education", "identity").Fulfilment);
        Assert.Equal(RequirementState.NeedsConfirmation, Line(draft, "general_education", "identity").State);
        Assert.Equal(RequirementState.Met, Line(verified, "general_education", "identity").State);
    }

    [Fact]
    public void A_critical_requirement_must_be_met_with_verified_facts_whatever_the_score()
    {
        // Everything but the two critical items is verified: the score is high, yet the content is not ready.
        var facts = new[] { Fact("Audience", "Adults."), Fact("Language", "English."), Fact("Brand", "Warm."), Fact("About", "A clinic.", BusinessFactStatus.Draft) };

        var type = Type(Evaluate(facts), "general_education");

        Assert.NotEqual(ContentReadinessStatus.Ready, type.Status);
    }

    [Fact]
    public void When_everything_critical_has_been_told_but_not_verified_the_status_says_it_only_needs_confirming()
    {
        var facts = BasicsVerified().Select(f => { f.Status = BusinessFactStatus.Draft; f.VerifiedAtUtc = null; return f; }).ToList();

        var type = Type(Evaluate(facts), "general_education");

        Assert.Equal(ContentReadinessStatus.NeedsConfirmation, type.Status);
        Assert.Contains("confirm", type.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ----- sources, evidence, freshness -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_ai_inference_earns_nothing_until_a_person_verifies_it()
    {
        var inferred = Fact("About", "An award-winning clinic.", BusinessFactStatus.Draft, BusinessFactOrigin.AiInferred);

        var before = Evaluate(new[] { inferred });
        Assert.Equal(0.0, Line(before, "general_education", "identity").Fulfilment);
        Assert.Equal(RequirementState.Missing, Line(before, "general_education", "identity").State);
        Assert.Equal(KnowledgeClassification.UnverifiedClaim, KnowledgeClassifier.Classify(inferred, Now));

        inferred.Status = BusinessFactStatus.Verified;
        inferred.VerifiedAtUtc = Now;
        var after = Evaluate(new[] { inferred });
        Assert.Equal(RequirementState.Met, Line(after, "general_education", "identity").State);
    }

    [Fact]
    public void A_customer_result_without_proof_does_not_count_and_asks_for_the_proof()
    {
        var result = Evaluate(new[] { Fact("Results", "Vision improved from 6/36 to 6/9.") });

        var line = Line(result, "case_studies", "customer_results");
        Assert.Equal(RequirementState.NeedsEvidence, line.State);
        Assert.Equal(0.0, line.Fulfilment);
        Assert.Equal(KnowledgeGapReason.NeedsEvidence, result.Gaps.Single(g => g.Key == "customer_results").Reason);
    }

    [Fact]
    public void A_customer_result_with_proof_and_consent_makes_a_case_study_ready()
    {
        var facts = BasicsVerified().Concat(new[]
        {
            Fact("Results", "Vision improved from 6/36 to 6/9 after surgery in March.", evidence: "Signed outcome report on file."),
            Fact("Consent", "Written consent signed on 12 March.", evidence: "Consent form 2026-031.")
        });

        var type = Type(Evaluate(facts), "case_studies");

        Assert.Equal(ContentReadinessStatus.Ready, type.Status);
        Assert.Equal(100, type.Score);
    }

    [Fact]
    public void A_fact_past_its_own_date_is_expired_and_counts_for_nothing()
    {
        var offer = Fact("Offers", "Free check-up until 30 September.", expiresAt: Now.AddDays(-17));

        var result = Evaluate(new[] { offer });

        Assert.Equal(KnowledgeClassification.Expired, KnowledgeClassifier.Classify(offer, Now));
        Assert.Equal(RequirementState.Expired, Line(result, "offers_pricing", "offers").State);
        Assert.Equal(0.0, Line(result, "offers_pricing", "offers").Fulfilment);
        Assert.Equal(1, result.Facts.Expired);
    }

    [Fact]
    public void A_verified_fact_older_than_the_rule_allows_is_outdated()
    {
        var fresh = Fact("Pricing", "Consultation Rs 500.", verifiedAt: Now.AddDays(-30));
        var stale = Fact("Pricing", "Consultation Rs 500.", verifiedAt: Now.AddDays(-400));

        Assert.Equal(RequirementState.Met, Line(Evaluate(new[] { fresh }), "offers_pricing", "pricing").State);

        var staleResult = Evaluate(new[] { stale });
        Assert.Equal(RequirementState.Outdated, Line(staleResult, "offers_pricing", "pricing").State);
        Assert.Equal(KnowledgeGapReason.Outdated, staleResult.Gaps.Single(g => g.Key == "pricing").Reason);
    }

    [Fact]
    public void A_retired_fact_is_ignored_entirely()
    {
        var result = Evaluate(new[] { Fact("About", "Old.", BusinessFactStatus.Retired) });

        Assert.Equal(RequirementState.Missing, Line(result, "general_education", "identity").State);
        Assert.Equal(1, result.Facts.Retired);
    }

    [Fact]
    public void A_fact_matches_a_requirement_by_the_question_it_came_from_as_well_as_by_category()
    {
        var byQuestion = Fact("Whatever", "We are a clinic.", sourceRef: "business_description");

        Assert.Equal(RequirementState.Met, Line(Evaluate(new[] { byQuestion }), "general_education", "identity").State);
    }

    // ----- the follow-up workflow ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_gap_carries_a_simple_question_the_reason_it_is_needed_and_what_it_unlocks()
    {
        var result = Evaluate(Array.Empty<BusinessFact>());

        var gap = result.Gaps.Single(g => g.Key == "pricing");
        Assert.False(string.IsNullOrWhiteSpace(gap.Question));
        Assert.False(string.IsNullOrWhiteSpace(gap.Why));
        Assert.Contains("Offers and pricing", gap.NeededFor);
        Assert.Equal(KnowledgeGapReason.Missing, gap.Reason);
        Assert.False(gap.Skippable);
    }

    [Fact]
    public void Gaps_that_block_the_most_important_content_come_first()
    {
        var result = Evaluate(Array.Empty<BusinessFact>());

        var firstSkippable = result.Gaps.ToList().FindIndex(g => g.Skippable);
        var firstCritical = result.Gaps.ToList().FindIndex(g => g.Key is "identity" or "services");
        Assert.True(firstCritical < firstSkippable);
    }

    [Fact]
    public void An_i_dont_know_answer_is_reported_as_that_not_as_nothing()
    {
        var unknown = Evaluate(Array.Empty<BusinessFact>(), specials: new Dictionary<string, string> { ["target_audience"] = "unknown", ["content_language"] = "need_help" });

        Assert.Equal(KnowledgeGapReason.Unknown, unknown.Gaps.Single(g => g.Key == "audience").Reason);
        Assert.Equal(KnowledgeGapReason.NeedsHelp, unknown.Gaps.Single(g => g.Key == "language").Reason);
        Assert.Contains("did not know", Line(unknown, "general_education", "audience").Explanation);
    }

    [Fact]
    public void Skipping_a_skippable_gap_takes_it_out_of_the_score_and_out_of_the_way()
    {
        var skip = new KnowledgeGapDecision { TenantId = Tenant, RequirementKey = "brand_voice", Kind = KnowledgeGapDecisionKind.Skipped };
        var facts = BasicsVerified().Where(f => f.Category != "Brand").ToList();

        var without = Evaluate(facts);
        var with = Evaluate(facts, new[] { skip });

        Assert.True(Type(with, "general_education").Score > Type(without, "general_education").Score);
        Assert.Equal(100, Type(with, "general_education").Score);
        Assert.Equal(RequirementState.Skipped, Line(with, "general_education", "brand_voice").State);
        Assert.DoesNotContain(with.Gaps, g => g.Key == "brand_voice");
    }

    [Fact]
    public void A_gap_the_rules_do_not_allow_skipping_cannot_be_skipped_by_a_stored_decision()
    {
        var skip = new KnowledgeGapDecision { TenantId = Tenant, RequirementKey = "pricing", Kind = KnowledgeGapDecisionKind.Skipped };

        var result = Evaluate(Array.Empty<BusinessFact>(), new[] { skip });

        Assert.Equal(RequirementState.Missing, Line(result, "offers_pricing", "pricing").State);
        Assert.Contains(result.Gaps, g => g.Key == "pricing");
    }

    [Fact]
    public void A_deferred_gap_is_marked_deferred_until_its_date_then_comes_back()
    {
        var defer = new KnowledgeGapDecision { TenantId = Tenant, RequirementKey = "pricing", Kind = KnowledgeGapDecisionKind.Deferred, DeferUntilUtc = Now.AddDays(3) };

        var during = Evaluate(Array.Empty<BusinessFact>(), new[] { defer });
        Assert.Equal(KnowledgeGapState.Deferred, during.Gaps.Single(g => g.Key == "pricing").State);

        defer.DeferUntilUtc = Now.AddDays(-1);
        var after = Evaluate(Array.Empty<BusinessFact>(), new[] { defer });
        Assert.Equal(KnowledgeGapState.Open, after.Gaps.Single(g => g.Key == "pricing").State);
    }

    [Fact]
    public void Supplying_the_missing_fact_recalculates_the_score_upwards()
    {
        var before = Evaluate(BasicsVerified().Where(f => f.Category != "Audience"));
        var after = Evaluate(BasicsVerified());

        Assert.True(Type(after, "general_education").Score > Type(before, "general_education").Score);
        Assert.Contains(before.Gaps, g => g.Key == "audience");
        Assert.DoesNotContain(after.Gaps, g => g.Key == "audience");
    }

    // ----- configurable rules -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_rules_come_from_the_setting_when_it_is_valid_and_fall_back_when_it_is_not()
    {
        var custom = """
        { "draftCredit": 0.0,
          "requirements": [ { "key": "r1", "label": "R1", "categories": ["X"], "question": "Q?", "why": "W." } ],
          "contentTypes": [ { "key": "t1", "label": "T1", "minScore": 50, "requirements": [ { "key": "r1", "weight": 10, "critical": true } ] } ] }
        """;

        var (rules, warning) = ReadinessRules.Resolve(custom);
        Assert.Null(warning);
        Assert.Single(rules.ContentTypes);

        var (_, brokenWarning) = ReadinessRules.Resolve("{ not json");
        Assert.Contains("not valid JSON", brokenWarning);

        var (fallback, unknownRef) = ReadinessRules.Resolve("""{ "requirements": [], "contentTypes": [] }""");
        Assert.NotNull(unknownRef);
        Assert.Equal(5, fallback.ContentTypes.Count);

        Assert.Equal(5, ReadinessRules.Resolve("  ").Rules.ContentTypes.Count);
    }

    [Fact]
    public void A_changed_rule_changes_the_score_for_the_same_facts()
    {
        var rules = ReadinessRules.Default;
        var facts = new[] { Fact("Services", "Cataract surgery.") };

        var one = Evaluate(facts, rules: rules);
        rules.Requirements.Single(r => r.Key == "services").MinFacts = 2;
        var two = Evaluate(facts, rules: rules);

        Assert.True(Line(two, "general_education", "services").Fulfilment < Line(one, "general_education", "services").Fulfilment);
    }

    [Fact]
    public void Invalid_rules_are_reported_with_every_problem()
    {
        var rules = new ReadinessRuleSet
        {
            DraftCredit = 1,
            Requirements = { new RequirementDef { Key = "Bad Key", Label = "", MinFacts = 0 } },
            ContentTypes = { new ContentTypeDef { Key = "t", Label = "T", MinScore = 0, Requirements = { new RequirementRef { Key = "missing", Weight = 0 } } } }
        };

        var problems = string.Join("|", ReadinessRules.Validate(rules));

        Assert.Contains("DraftCredit", problems);
        Assert.Contains("Bad Key", problems);
        Assert.Contains("MinFacts", problems);
        Assert.Contains("MinScore", problems);
        Assert.Contains("unknown requirement", problems);
        Assert.Contains("weight", problems);
    }

    // ----- classification ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Facts_are_classified_by_status_origin_and_date()
    {
        Assert.Equal(KnowledgeClassification.TenantProvided, KnowledgeClassifier.Classify(Fact("About", status: BusinessFactStatus.Draft), Now));
        Assert.Equal(KnowledgeClassification.SystemDerived, KnowledgeClassifier.Classify(Fact("About", status: BusinessFactStatus.Draft, origin: BusinessFactOrigin.SystemDerived), Now));
        Assert.Equal(KnowledgeClassification.UnverifiedClaim, KnowledgeClassifier.Classify(Fact("About", status: BusinessFactStatus.Draft, origin: BusinessFactOrigin.AiInferred), Now));
        Assert.Equal(KnowledgeClassification.TenantApproved, KnowledgeClassifier.Classify(Fact("About"), Now));
        Assert.Equal(KnowledgeClassification.Expired, KnowledgeClassifier.Classify(Fact("About", expiresAt: Now.AddMinutes(-1)), Now));
        Assert.Equal(KnowledgeClassification.Retired, KnowledgeClassifier.Classify(Fact("About", status: BusinessFactStatus.Retired), Now));
        Assert.True(KnowledgeClassifier.IsUsable(Fact("About", expiresAt: Now.AddDays(1)), Now));
        Assert.False(KnowledgeClassifier.IsUsable(Fact("About", status: BusinessFactStatus.Draft), Now));
    }

    // ----- claims ---------------------------------------------------------------------------------------------------------------------------

    private static ClaimCheckDto CheckOne(string claim, params BusinessFact[] facts) =>
        KnowledgeClaimChecker.Check(new[] { claim }, facts, Now).Claims.Single();

    [Fact]
    public void A_claim_matching_a_verified_fact_is_supported_and_names_the_fact()
    {
        var fact = Fact("Services", "We perform cataract surgery and children's eye checks.");

        var check = CheckOne("We perform cataract surgery.", fact);

        Assert.Equal(ClaimVerdict.Supported, check.Verdict);
        Assert.Equal(new[] { fact.Id }, check.SupportingFactIds);
    }

    [Fact]
    public void A_claim_nothing_supports_is_flagged_not_silently_accepted()
    {
        var check = CheckOne("We offer free LASIK for everyone.", Fact("Services", "We perform cataract surgery."));

        Assert.Equal(ClaimVerdict.Unsupported, check.Verdict);
        Assert.Empty(check.SupportingFactIds);
    }

    [Fact]
    public void A_claim_that_only_an_unverified_fact_supports_is_reported_as_unverified()
    {
        var draft = Fact("Services", "We perform cataract surgery.", BusinessFactStatus.Draft);

        var check = CheckOne("We perform cataract surgery.", draft);

        Assert.Equal(ClaimVerdict.Unverified, check.Verdict);
        Assert.Equal(new[] { draft.Id }, check.SupportingFactIds);
    }

    [Fact]
    public void A_claim_that_only_an_expired_fact_supports_is_not_supported()
    {
        var expired = Fact("Offers", "Free check-up for senior citizens.", expiresAt: Now.AddDays(-1));

        Assert.Equal(ClaimVerdict.Unverified, CheckOne("Free check-up for senior citizens.", expired).Verdict);
    }

    [Fact]
    public void A_figure_that_is_not_in_the_facts_is_never_supported_even_when_the_topic_is()
    {
        var fact = Fact("Pricing", "Cataract surgery starts at Rs 25,000 per eye.");

        Assert.Equal(ClaimVerdict.Supported, CheckOne("Cataract surgery starts at Rs 25,000 per eye.", fact).Verdict);

        var wrong = CheckOne("Cataract surgery starts at Rs 15,000 per eye.", fact);
        Assert.Equal(ClaimVerdict.Unsupported, wrong.Verdict);
        Assert.Contains("numbers", wrong.Reason);
    }

    [Fact]
    public void A_result_or_guarantee_needs_a_fact_that_has_proof_not_just_a_matching_fact()
    {
        var withoutProof = Fact("Results", "98% of patients see better after surgery.");
        var withProof = Fact("Results", "98% of patients see better after surgery.", evidence: "Audited outcome register 2025.");

        var unproven = CheckOne("98% of patients see better after surgery.", withoutProof);
        Assert.Equal(ClaimVerdict.Unverified, unproven.Verdict);
        Assert.True(unproven.IsRisky);

        Assert.Equal(ClaimVerdict.Supported, CheckOne("98% of patients see better after surgery.", withProof).Verdict);
    }

    [Fact]
    public void The_summary_counts_what_passed_and_what_was_flagged()
    {
        var facts = new[] { Fact("Services", "We perform cataract surgery.") };

        var result = KnowledgeClaimChecker.Check(new[] { "We perform cataract surgery.", "We cure every disease.", "  " }, facts, Now);

        Assert.Equal(2, result.Claims.Count);
        Assert.Equal(1, result.Supported);
        Assert.Equal(1, result.Flagged);
    }
}
