namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>
/// Decides how many candidates one research round should ask the agent for. A candidate that is rejected, a duplicate, or kept only as a lead does
/// not count toward the batch, and none of them is charged to the tenant - so the platform pays to research them. Asking for exactly the number
/// still needed would mean another round for every one of them; asking for far more pays for qualified candidates that are then not used. The sweet
/// spot depends on how many candidates this tenant's searches usually throw away, so it is estimated from that:
///
///   ask = still needed / (1 - unusable rate)
///
/// With 10 needed and 2 in every 12 candidates unusable (17%), that is 12. The estimate starts from the tenant's recent real runs (or a configured
/// starting guess when there are none) and is corrected by what the run's own rounds turn up, so a run that is finding many rejects asks for
/// more, and one that is not asks for little more than it needs. <c>CandidateHeadroomFactor</c> stays as the ceiling.
/// </summary>
public static class CandidateRequestPlanner
{
    /// <summary>How much a history or a starting guess is worth against what this run has seen so far, in candidates: a prior of "30%" counts as
    /// if this many candidates had already been looked at. It keeps one lucky round from collapsing the estimate to zero.</summary>
    public const int PriorWeight = 10;

    /// <summary>An estimate is never taken above this: at 90% the arithmetic would ask for ten times what is needed.</summary>
    public const decimal MaxRate = 0.9m;

    /// <summary>
    /// The share of candidates expected to be unusable, 0 to <see cref="MaxRate"/>: the prior blended with what the run has seen, each
    /// weighted by how many candidates it rests on.
    /// </summary>
    /// <param name="priorRate">From recent runs, or the starting guess. 0-1.</param>
    /// <param name="observedCandidates">Candidates this run has considered so far.</param>
    /// <param name="observedUnusable">Of those, the ones that did not count: rejected, duplicate, or kept without a customer.</param>
    public static decimal ExpectedUnusableRate(decimal priorRate, int observedCandidates, int observedUnusable)
    {
        var prior = Math.Clamp(priorRate, 0m, 1m);
        var seen = Math.Max(0, observedCandidates);
        var unusable = Math.Clamp(observedUnusable, 0, seen);

        var rate = (prior * PriorWeight + unusable) / (PriorWeight + seen);
        return Math.Clamp(rate, 0m, MaxRate);
    }

    /// <summary>
    /// How many candidates to ask for to end up with <paramref name="stillNeeded"/> usable ones, never fewer than that and never more than the
    /// ceiling: <paramref name="perRoundCap"/> candidates, or <paramref name="headroomFactor"/> times what is needed, whichever is smaller.
    /// </summary>
    public static int Candidates(int stillNeeded, decimal unusableRate, int perRoundCap, int headroomFactor)
    {
        if (stillNeeded <= 0)
            return 1;

        var rate = Math.Clamp(unusableRate, 0m, MaxRate);
        // Rounded before the ceiling so 10 / (1 - 2/12), which is 12 and a hair, does not become 13.
        var wanted = (int)Math.Ceiling(Math.Round(stillNeeded / (1m - rate), 4));

        var ceiling = Math.Min(Math.Max(1, perRoundCap), stillNeeded * Math.Max(1, headroomFactor));
        return Math.Max(1, Math.Min(Math.Max(stillNeeded, wanted), Math.Max(ceiling, 1)));
    }

    /// <summary>The rate recent runs suggest: the unusable share of all candidates they considered, or null when there are too few to say.</summary>
    /// <param name="runs">(candidates considered, rejected, duplicates) of each recent run.</param>
    /// <param name="minimumCandidates">Fewer than this many in all is not enough to go on.</param>
    public static decimal? RateFromHistory(IEnumerable<(int Considered, int Rejected, int Duplicates)> runs, int minimumCandidates)
    {
        var considered = 0;
        var unusable = 0;
        foreach (var (c, rejected, duplicates) in runs)
        {
            considered += Math.Max(0, c);
            unusable += Math.Max(0, rejected) + Math.Max(0, duplicates);
        }

        return considered < Math.Max(1, minimumCandidates) ? null : Math.Clamp((decimal)unusable / considered, 0m, 1m);
    }
}
