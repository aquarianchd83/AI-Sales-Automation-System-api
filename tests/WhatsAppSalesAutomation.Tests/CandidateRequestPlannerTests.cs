using WhatsAppSalesAutomation.Application.LeadDiscovery;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>How many candidates a research round asks for: enough to cover what is usually thrown away, no more. The numbers are the worked
/// examples the design was agreed on.</summary>
public sealed class CandidateRequestPlannerTests
{
    // Per round the agent is asked for at most 20, and never more than 3 times what is still needed.
    private const int Cap = 20;
    private const int Factor = 3;

    [Fact]
    public void Example_A_low_rejection_10_needed_and_2_in_12_thrown_away_asks_for_12_not_20()
    {
        var rate = 2m / 12m;                                   // 17%

        Assert.Equal(12, CandidateRequestPlanner.Candidates(10, rate, Cap, Factor));
    }

    [Fact]
    public void Example_B_high_rejection_asks_for_a_lot_at_first_then_only_for_what_is_still_missing()
    {
        // 60% thrown away: 10 needed would be 25, but a round asks for at most 20.
        Assert.Equal(20, CandidateRequestPlanner.Candidates(10, 0.6m, Cap, Factor));
        // 8 got through; 2 more are needed, and 2 / 0.4 is 5 - not another 20.
        Assert.Equal(5, CandidateRequestPlanner.Candidates(2, 0.6m, Cap, Factor));
    }

    [Theory]
    [InlineData(10, 0.0, 10)]    // nothing is thrown away: ask for exactly what is needed
    [InlineData(10, 0.3, 15)]    // the starting guess: 10 / 0.7 = 14.3
    [InlineData(10, 0.5, 20)]    // 10 / 0.5
    [InlineData(4, 0.1875, 5)]   // 4 / 0.8125 = 4.9
    [InlineData(1, 0.3, 2)]      // 1 / 0.7 = 1.4
    [InlineData(3, 0.0, 3)]
    public void It_asks_for_what_is_needed_divided_by_the_share_that_survives(int needed, double rate, int expected) =>
        Assert.Equal(expected, CandidateRequestPlanner.Candidates(needed, (decimal)rate, Cap, Factor));

    [Fact]
    public void It_never_asks_for_fewer_than_what_is_still_needed()
    {
        Assert.Equal(7, CandidateRequestPlanner.Candidates(7, 0m, Cap, Factor));
        Assert.True(CandidateRequestPlanner.Candidates(7, 0.01m, Cap, Factor) >= 7);
    }

    [Fact]
    public void The_headroom_factor_is_a_ceiling_so_a_bad_estimate_cannot_ask_for_too_much()
    {
        // 80% thrown away would ask for 10 / 0.2 = 50, but the ceiling is 2 x 10 = 20 (and the per-round cap is 20).
        Assert.Equal(20, CandidateRequestPlanner.Candidates(10, 0.8m, 50, 2));
        Assert.Equal(15, CandidateRequestPlanner.Candidates(5, 0.8m, 50, 3));   // 5 / 0.2 = 25, ceiling 3 x 5
    }

    [Fact]
    public void A_factor_of_one_asks_for_exactly_what_is_needed_whatever_the_estimate()
    {
        Assert.Equal(10, CandidateRequestPlanner.Candidates(10, 0.5m, Cap, 1));
        Assert.Equal(10, CandidateRequestPlanner.Candidates(10, 0.9m, Cap, 1));
    }

    [Fact]
    public void A_batch_bigger_than_a_round_can_ask_for_is_filled_over_several_rounds()
    {
        Assert.Equal(20, CandidateRequestPlanner.Candidates(30, 0m, Cap, Factor));
    }

    [Fact]
    public void An_estimate_above_the_maximum_is_taken_as_the_maximum_so_the_arithmetic_cannot_blow_up()
    {
        // 100% thrown away would divide by zero.
        Assert.Equal(20, CandidateRequestPlanner.Candidates(10, 1m, Cap, Factor));
        Assert.Equal(20, CandidateRequestPlanner.Candidates(10, 5m, Cap, Factor));
        Assert.Equal(10, CandidateRequestPlanner.Candidates(10, -1m, Cap, Factor));
    }

    [Fact]
    public void Nothing_still_needed_still_asks_for_one_rather_than_zero_or_a_negative_number()
    {
        Assert.Equal(1, CandidateRequestPlanner.Candidates(0, 0.3m, Cap, Factor));
        Assert.Equal(1, CandidateRequestPlanner.Candidates(-3, 0.3m, Cap, Factor));
    }

    // ---- the estimate ----

    [Fact]
    public void With_nothing_seen_yet_the_estimate_is_the_prior()
    {
        Assert.Equal(0.3m, CandidateRequestPlanner.ExpectedUnusableRate(0.3m, 0, 0));
    }

    [Fact]
    public void A_run_that_throws_a_lot_away_pushes_the_estimate_up_and_one_that_does_not_pulls_it_down()
    {
        var prior = 0.3m;

        var heavy = CandidateRequestPlanner.ExpectedUnusableRate(prior, 15, 15);   // everything rejected
        var light = CandidateRequestPlanner.ExpectedUnusableRate(prior, 6, 0);     // nothing rejected

        Assert.Equal(0.72m, heavy);                       // (0.3 x 10 + 15) / (10 + 15)
        Assert.Equal(0.1875m, light);                     // (0.3 x 10 + 0) / (10 + 6)
        Assert.True(heavy > prior && light < prior);
    }

    [Fact]
    public void One_lucky_round_does_not_collapse_the_estimate_to_zero()
    {
        var afterOneLuckyRound = CandidateRequestPlanner.ExpectedUnusableRate(0.4m, 5, 0);

        Assert.True(afterOneLuckyRound > 0.2m);           // the prior still counts for something
    }

    [Fact]
    public void The_more_the_run_has_seen_the_more_the_run_outweighs_the_prior()
    {
        var early = CandidateRequestPlanner.ExpectedUnusableRate(0.1m, 10, 10);   // everything thrown away, 10 seen
        var late = CandidateRequestPlanner.ExpectedUnusableRate(0.1m, 100, 100);

        Assert.True(late > early);
        Assert.True(late > 0.85m);
    }

    [Fact]
    public void The_estimate_is_kept_inside_zero_to_the_maximum_whatever_it_is_given()
    {
        Assert.Equal(CandidateRequestPlanner.MaxRate, CandidateRequestPlanner.ExpectedUnusableRate(1m, 100, 100));
        Assert.Equal(0m, CandidateRequestPlanner.ExpectedUnusableRate(-1m, 0, 0));
        // More unusable than considered cannot happen; it is counted as "all of them" (10 of 10), not as 999.
        Assert.Equal(0.75m, CandidateRequestPlanner.ExpectedUnusableRate(0.5m, 10, 999));   // (0.5 x 10 + 10) / (10 + 10)
    }

    // ---- learning it from earlier runs ----

    [Fact]
    public void The_rate_from_history_is_the_share_of_everything_considered_that_was_thrown_away()
    {
        var rate = CandidateRequestPlanner.RateFromHistory(new[] { (12, 2, 0), (8, 1, 1) }, minimumCandidates: 10);

        Assert.Equal(4m / 20m, rate);                     // (2 + 0 + 1 + 1) / (12 + 8)
    }

    [Fact]
    public void Too_little_history_says_nothing_so_the_starting_guess_is_used_instead()
    {
        Assert.Null(CandidateRequestPlanner.RateFromHistory(new[] { (6, 3, 0) }, minimumCandidates: 10));
        Assert.Null(CandidateRequestPlanner.RateFromHistory(Array.Empty<(int, int, int)>(), minimumCandidates: 10));
    }

    [Fact]
    public void A_run_that_considered_nothing_adds_nothing_to_the_history()
    {
        var rate = CandidateRequestPlanner.RateFromHistory(new[] { (0, 0, 0), (20, 4, 0) }, minimumCandidates: 10);

        Assert.Equal(0.2m, rate);
    }
}
