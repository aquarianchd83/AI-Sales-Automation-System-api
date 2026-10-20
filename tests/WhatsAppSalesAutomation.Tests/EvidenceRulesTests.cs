using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The rules that decide whether an answer is evidenced: finding the right passages, checking quotations against them, checking figures,
/// spotting disagreeing sources and - from only what was verified - the confidence. No database, no AI: the same rules apply to every provider.</summary>
public sealed class EvidenceRulesTests
{
    private static PassageCandidate Passage(string text, string? heading = null, string source = "brochure.pdf", string location = "Page 1", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), source, 1, location, heading, text, PassageSplitter.HashOf(text));

    // ----- finding passages by meaning too ----------------------------------------------------------------------------------------------

    private static (PassageCandidate Passage, float[] Vector) WithVector(string text, params float[] v) => (Passage(text), v);

    private static IReadOnlyDictionary<string, float[]> Vectors(params (PassageCandidate Passage, float[] Vector)[] items) =>
        items.ToDictionary(i => i.Passage.Hash, i => i.Vector);

    [Fact]
    public void A_passage_that_means_the_same_is_found_even_when_it_shares_no_word_with_the_question()
    {
        var hinglish = WithVector("Ek visit ki keemat paanch sau rupaye hai.", 0.9f, 0.436f);
        var parking = WithVector("Parking is free for patients.", 0.0f, 1.0f);
        var all = new[] { hinglish.Passage, parking.Passage };

        Assert.Empty(PassageRetriever.Rank("How much does a visit cost?", null, all, 3)); // words alone find nothing

        var ranked = PassageRetriever.RankHybrid("How much does a visit cost?", null, all, 3, new[] { 1.0f, 0.0f }, Vectors(hinglish, parking));

        Assert.Equal(hinglish.Passage.Id, Assert.Single(ranked).Passage.Id); // the unrelated one is below the similarity floor
    }

    [Fact]
    public void A_passage_that_words_and_meaning_both_find_ranks_above_one_that_only_one_finds()
    {
        var both = WithVector("The consultation fee is Rs 500 for the first visit.", 0.8f, 0.6f);
        var wordsOnly = WithVector("The fee for lab tests is shown at reception.", 0.0f, 1.0f);
        var meaningOnly = WithVector("Rates for an appointment start at five hundred.", 0.95f, 0.312f);
        var all = new[] { wordsOnly.Passage, meaningOnly.Passage, both.Passage };

        var ranked = PassageRetriever.RankHybrid("What is the consultation fee?", null, all, 3, new[] { 1.0f, 0.0f }, Vectors(both, wordsOnly, meaningOnly));

        Assert.Equal(both.Passage.Id, ranked[0].Passage.Id);
        Assert.Contains(ranked, r => r.Passage.Id == wordsOnly.Passage.Id);
        Assert.Contains(ranked, r => r.Passage.Id == meaningOnly.Passage.Id);
    }

    [Fact]
    public void A_passage_with_no_vector_is_still_found_by_its_words_and_one_with_a_vector_of_the_wrong_size_is_ignored()
    {
        var fee = Passage("The consultation fee is Rs 500 for the first visit.");
        var wrongSize = WithVector("Appointments cost five hundred.", 1f, 0f, 0f);

        var ranked = PassageRetriever.RankHybrid("What is the consultation fee?", null, new[] { fee, wrongSize.Passage }, 3, new[] { 1.0f, 0.0f }, Vectors(wrongSize));

        Assert.Equal(fee.Id, Assert.Single(ranked).Passage.Id);
    }

    [Fact]
    public void With_no_question_vector_or_no_passage_vectors_the_result_is_exactly_the_word_search()
    {
        var fee = Passage("The consultation fee is Rs 500 for the first visit.");
        var parking = Passage("Parking is free for patients.");
        var all = new[] { parking, fee };
        var expected = PassageRetriever.Rank("What is the consultation fee?", null, all, 3).Select(r => (r.Passage.Id, r.Score)).ToList();

        var noQuery = PassageRetriever.RankHybrid("What is the consultation fee?", null, all, 3, null, new Dictionary<string, float[]>());
        var noVectors = PassageRetriever.RankHybrid("What is the consultation fee?", null, all, 3, new[] { 1.0f, 0.0f }, null);
        var emptyVectors = PassageRetriever.RankHybrid("What is the consultation fee?", null, all, 3, new[] { 1.0f, 0.0f }, new Dictionary<string, float[]>());

        Assert.Equal(expected, noQuery.Select(r => (r.Passage.Id, r.Score)).ToList());
        Assert.Equal(expected, noVectors.Select(r => (r.Passage.Id, r.Score)).ToList());
        Assert.Equal(expected, emptyVectors.Select(r => (r.Passage.Id, r.Score)).ToList());
    }

    [Fact]
    public void The_number_of_passages_offered_is_the_number_asked_for()
    {
        var all = Enumerable.Range(0, 8).Select(i => Passage($"The consultation fee number {i} is Rs 500.")).ToArray();

        Assert.Equal(2, PassageRetriever.RankHybrid("consultation fee", null, all, 2, null, null).Count);
        Assert.Equal(8, PassageRetriever.RankHybrid("consultation fee", null, all, 20, null, null).Count);
    }

    // ----- quotations -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_quotation_that_is_in_the_passage_is_cut_from_the_passages_own_words()
    {
        const string text = "Welcome to our clinic.\n\nThe CONSULTATION fee is Rs 500, for the first visit only. Follow-ups are free.";

        var excerpt = QuoteVerifier.Locate(text, "consultation fee is rs 500 for the first visit");

        Assert.Equal("CONSULTATION fee is Rs 500, for the first visit", excerpt); // the passage's words, not the model's
    }

    [Theory]
    [InlineData("The consultation fee is Rs 800 for the first visit")] // a changed figure: ten words, nine right - and still refused
    [InlineData("We offer free parking for all patients")] // never said
    [InlineData("Rs")] // one word proves nothing
    [InlineData("")]
    [InlineData(null)]
    public void An_invented_or_empty_quotation_is_refused(string? quote)
    {
        Assert.Null(QuoteVerifier.Locate("The consultation fee is Rs 500, for the first visit only.", quote));
    }

    [Fact]
    public void A_long_quotation_that_differs_by_a_word_is_accepted_but_a_short_one_must_be_exact()
    {
        const string text = "Our hospital has twenty years of experience in eye care and treats thousands of patients across Maharashtra every year.";

        Assert.NotNull(QuoteVerifier.Locate(text, "Our hospital has twenty years of experience in eye care and treats thousands of patients throughout Maharashtra every year"));
        Assert.Null(QuoteVerifier.Locate(text, "twenty years of surgery"));
    }

    // ----- finding passages ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_passages_that_bear_on_the_question_come_first_and_unrelated_ones_are_left_out()
    {
        var fee = Passage("The consultation fee is Rs 500 for the first visit.");
        var timings = Passage("The clinic is open Monday to Saturday, 9 am to 6 pm.");
        var parking = Passage("Free parking is available behind the building.");

        var ranked = PassageRetriever.Rank("What is the consultation fee?", null, new[] { parking, timings, fee }, 3);

        Assert.Equal(fee.Id, Assert.Single(ranked).Passage.Id);
    }

    [Fact]
    public void A_generic_standard_question_finds_passages_through_its_hint_words()
    {
        var benefit = Passage("The software helps clinics save time and improve patient care, a key advantage for busy doctors.", "Why choose us");
        var address = Passage("Our office is on Station Road, near the old market.");

        var ranked = PassageRetriever.Rank("What is the main benefit of your product?", "benefit advantage helps improve value main key", new[] { address, benefit }, 3);

        Assert.Equal(benefit.Id, ranked.First().Passage.Id);
        Assert.DoesNotContain(ranked, r => r.Passage.Id == address.Id);
    }

    [Fact]
    public void A_heading_counts_as_part_of_what_a_passage_is_about_and_nothing_is_returned_when_nothing_matches()
    {
        var byHeading = Passage("Starts at Rs 999 a month.", "Pricing plans");
        Assert.Single(PassageRetriever.Rank("What are the pricing plans?", null, new[] { byHeading }, 3));

        Assert.Empty(PassageRetriever.Rank("Do you deliver internationally?", null, new[] { byHeading }, 3));
        Assert.Empty(PassageRetriever.Rank("anything", null, Array.Empty<PassageCandidate>(), 3));
        Assert.Empty(PassageRetriever.Rank("the of and", null, new[] { byHeading }, 3)); // only stop words
    }

    // ----- figures ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Prices_percentages_and_durations_are_figures_but_a_small_count_is_not()
    {
        Assert.Equal(new[] { "25000", "30", "6" }, ClaimRules.Figures("Surgery from Rs 25,000, 30% off, results in 6 months, in 3 easy steps."));
    }

    [Fact]
    public void A_figure_in_the_answer_must_appear_in_the_evidence_however_it_is_written()
    {
        var evidence = new[] { "Cataract surgery starts at Rs 25,000 per eye." };

        Assert.Empty(ClaimRules.Unsupported("Surgery is from ₹25000 per eye.", evidence));
        Assert.Equal(new[] { "20" }, ClaimRules.Unsupported("From Rs 25,000, and 20% cheaper than others.", evidence));
        Assert.Equal(new[] { "2500" }, ClaimRules.Unsupported("It costs Rs 2,500.", evidence)); // 2,500 is not 25,000
    }

    [Theory]
    [InlineData("The consultation fee is Rs 500.", true)]
    [InlineData("We guarantee results.", true)]
    [InlineData("Our lab is ISO 9001 certified.", true)]
    [InlineData("Customers save 30% on costs.", true)]
    [InlineData("The clinic has two waiting rooms.", false)]
    public void Price_guarantee_certification_and_outcome_claims_are_material(string text, bool material)
    {
        Assert.Equal(material, ClaimRules.IsMaterial(text));
    }

    // ----- confidence ----------------------------------------------------------------------------------------------------------------

    private static ConfidenceInput Input(
        string? answer, EvidenceSnippet[] evidence, bool inference = false, bool insufficient = false, bool conflict = false, bool unsupported = false, bool simulated = false) =>
        new(answer, evidence, inference, insufficient, conflict, unsupported, simulated);

    private static EvidenceSnippet Snippet(string excerpt, Guid? passage = null) => new(passage ?? Guid.NewGuid(), excerpt, excerpt);

    private const string Fee = "The consultation fee is Rs 500 for the first visit.";

    [Fact]
    public void A_single_passage_that_states_the_answer_directly_is_high()
    {
        var result = ConfidenceEngine.Assess(Input("The consultation fee is Rs 500 for the first visit.", new[] { Snippet(Fee) }));

        Assert.Equal(KnowledgeConfidence.High, result.Confidence);
        Assert.Equal(KnowledgeAnswerKind.SourceExtracted, result.Kind);
    }

    [Fact]
    public void An_interpretation_or_a_partly_covered_answer_is_medium()
    {
        Assert.Equal(KnowledgeConfidence.Medium, ConfidenceEngine.Assess(Input("The consultation fee is Rs 500 for the first visit.", new[] { Snippet(Fee) }, inference: true)).Confidence);

        var partly = ConfidenceEngine.Assess(Input("The consultation fee is Rs 500 and patients also get free parking and priority booking.", new[] { Snippet(Fee) }));
        Assert.Equal(KnowledgeConfidence.Medium, partly.Confidence);
        Assert.Equal(KnowledgeAnswerKind.AiInterpreted, partly.Kind);
    }

    [Fact]
    public void Combining_three_or_more_passages_is_never_high()
    {
        var evidence = new[] { Snippet(Fee), Snippet(Fee), Snippet(Fee) };

        Assert.Equal(KnowledgeConfidence.Medium, ConfidenceEngine.Assess(Input("The consultation fee is Rs 500 for the first visit.", evidence)).Confidence);
    }

    [Fact]
    public void An_unproven_figure_or_a_conflict_makes_it_low_however_well_the_rest_is_covered()
    {
        var answer = "The consultation fee is Rs 500 for the first visit.";

        Assert.Equal(KnowledgeConfidence.Low, ConfidenceEngine.Assess(Input(answer, new[] { Snippet(Fee) }, unsupported: true)).Confidence);
        Assert.Equal(KnowledgeConfidence.Low, ConfidenceEngine.Assess(Input(answer, new[] { Snippet(Fee) }, conflict: true)).Confidence);
        Assert.Equal(KnowledgeConfidence.Low, ConfidenceEngine.Assess(Input(answer, new[] { Snippet(Fee) }, insufficient: true)).Confidence);
    }

    [Fact]
    public void Without_a_verified_quotation_there_is_no_evidence_whatever_the_model_claimed()
    {
        var result = ConfidenceEngine.Assess(Input("The consultation fee is Rs 500.", Array.Empty<EvidenceSnippet>()));

        Assert.Equal(KnowledgeConfidence.NoEvidence, result.Confidence);
        Assert.Equal(KnowledgeConfidence.NoEvidence, ConfidenceEngine.Assess(Input(null, new[] { Snippet(Fee) })).Confidence);
    }

    [Fact]
    public void The_simulator_can_never_reach_high()
    {
        var result = ConfidenceEngine.Assess(Input("The consultation fee is Rs 500 for the first visit.", new[] { Snippet(Fee) }, simulated: true));

        Assert.Equal(KnowledgeConfidence.Medium, result.Confidence);
        Assert.Contains(result.Reasons, r => r.Contains("simulator"));
    }

    // ----- conflicts -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_sources_giving_different_prices_for_the_same_thing_are_a_conflict_with_both_values_and_places()
    {
        var brochure = Passage("The Premium Plan costs Rs 999 per month.", source: "brochure.pdf", location: "Page 2");
        var website = Passage("The Premium Plan costs Rs 1,499 per month.", source: "clinic.example", location: "https://clinic.example/pricing");

        var conflict = Assert.Single(ConflictDetector.Detect(new[] { brochure, website }));

        Assert.Equal("price", conflict.Fact);
        Assert.Equal(new[] { "999", "1499" }, conflict.Values.Select(v => v.Value));
        Assert.Equal(new[] { "brochure.pdf", "clinic.example" }, conflict.Values.Select(v => v.SourceName));
        Assert.Equal("Page 2", conflict.Values[0].Location);
    }

    [Fact]
    public void Different_things_the_same_value_or_one_passage_are_not_conflicts()
    {
        var premium = Passage("The Premium Plan costs Rs 999 per month.");
        var basic = Passage("The Starter Subscription for freelancers costs Rs 299 per month.");
        var same = Passage("The Premium Plan costs Rs 999 per month, billed monthly.");
        var both = Passage("The Premium Plan costs Rs 999 per month. The Premium Plan costs Rs 1,499 per month.");

        Assert.Empty(ConflictDetector.Detect(new[] { premium, basic }));
        Assert.Empty(ConflictDetector.Detect(new[] { premium, same }));
        Assert.Empty(ConflictDetector.Detect(new[] { both }));
    }

    // ----- the questions and the model's reply ---------------------------------------------------------------------------------------

    [Fact]
    public void The_twelve_standard_questions_are_all_there_each_with_a_way_to_ask_the_tenant()
    {
        Assert.Equal(12, StandardQuestions.All.Count);
        Assert.Equal(12, StandardQuestions.All.Select(q => q.Key).Distinct().Count());
        Assert.All(StandardQuestions.All, q =>
        {
            Assert.EndsWith("?", q.Text);
            Assert.False(string.IsNullOrWhiteSpace(q.Hints));
            Assert.False(string.IsNullOrWhiteSpace(q.Clarifying));
        });
        Assert.Contains(StandardQuestions.All, q => q.Text == "What should a prospective customer do next?");
        Assert.Contains(StandardQuestions.All, q => q.Text == "What evidence supports the product's claimed benefits?");
    }

    [Fact]
    public void The_models_reply_is_read_tolerantly_and_a_reply_that_is_not_an_object_is_refused()
    {
        var reply = DraftPrompts.ParseAnswer("""{"answer":" Rs 500. ","insufficientEvidence":false,"inference":true,"evidence":[{"passage":"P1","quote":"fee is Rs 500"},{"passage":1,"quote":"x"},{"nope":true}],"extra":1}""")!;

        Assert.Equal("Rs 500.", reply.Answer);
        Assert.True(reply.Inference);
        Assert.Equal(("P1", "fee is Rs 500"), (reply.Evidence[0].Passage, reply.Evidence[0].Quote));
        Assert.Single(reply.Evidence);

        Assert.Null(DraftPrompts.ParseAnswer("[1,2]"));
        Assert.Null(DraftPrompts.ParseAnswer("not json"));
        Assert.Null(DraftPrompts.ParseAnswer(null));
        Assert.Empty(DraftPrompts.ParseFeatures("""{"features":"none"}""")!);
    }

    [Fact]
    public void The_prompt_numbers_the_passages_and_tells_the_model_they_are_data_not_instructions()
    {
        var text = DraftPrompts.AnswerUser("What is the fee?", new[] { Passage("Fee is Rs 500.", "Pricing", "brochure.pdf", "Page 2") });

        Assert.Contains("[P1] (brochure.pdf, Page 2 - Pricing)", text);
        Assert.Contains("QUESTION: What is the fee?", text);
        Assert.Contains("ignore them", DraftPrompts.AnswerSystem);
        Assert.Contains("insufficientEvidence", DraftPrompts.AnswerSystem);
    }
}
