using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

/// <summary>
/// Replaces the hardcoded <c>+30/+30/+20, ±20</c> heuristic that used to live in LeadService with the
/// tenant's own configuration.
///
/// The score is the sum of recorded contributions, not a formula re-evaluated over the whole
/// conversation. That difference is deliberate and is the one real behaviour change in this work: the
/// old heuristic recomputed from scratch each turn, so a lead that negotiated and then asked an
/// unrelated question silently lost its negotiation bonus. Accumulating means a signal, once earned,
/// stays earned - which is what a salesperson reading the lead already assumes is happening.
///
/// Field weights are what preserve the old numbers. A lead with budget, interest and timeline captured
/// still totals 30 + 30 + 20, because the seeded fields carry exactly those weights.
/// </summary>
public class LeadScoringService : ILeadScoringService
{
    /// <summary>Kept in one place so <see cref="LeadScoreBand"/> and the hot-lead trigger cannot drift
    /// apart - "hot" on the pipeline board and "hot enough to stop qualifying" must mean the same
    /// thing, or the board and the agent will disagree in front of the customer.</summary>
    public const int HotBandThreshold = 70;
    public const int WarmBandThreshold = 40;

    public const int MinScore = 0;
    public const int MaxScore = 100;

    private const string FieldPrefix = "field:";
    private const string RulePrefix = "rule:";

    /// <summary>Intents that mean the customer is ready to act, whatever the numeric score says.
    /// A demo request from someone who has told us nothing else is still a demo request.</summary>
    private static readonly IReadOnlySet<CustomerIntent> BuyingIntents = new HashSet<CustomerIntent>
    {
        CustomerIntent.PurchaseIntent,
        CustomerIntent.DemoRequest,
        CustomerIntent.AppointmentRequest,
        CustomerIntent.SiteVisit,
        CustomerIntent.Booking
    };

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantConfigOverrideProvider _tenantConfig;

    public LeadScoringService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        ITenantConfigOverrideProvider tenantConfig)
    {
        _context = context;
        _dateTime = dateTime;
        _tenantConfig = tenantConfig;
    }

    public async Task<LeadScoreResult> RecomputeAsync(
        Guid leadId, ScoringSignals signals, CancellationToken cancellationToken = default)
    {
        var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken)
            ?? throw new NotFoundException(nameof(Lead), leadId);

        var options = await _tenantConfig.GetAiOptionsAsync(cancellationToken);
        var now = _dateTime.UtcNow;

        var captured = await _context.LeadQualificationValues
            .Where(v => v.LeadId == leadId && !v.IsSuperseded)
            .ToListAsync(cancellationToken);

        // A value the model was unsure about is on record but earns nothing - see
        // AiOptions.MinFieldExtractionConfidence.
        var known = captured
            .Where(v => v.ExtractionConfidence >= options.MinFieldExtractionConfidence)
            .ToList();

        var existing = await _context.LeadScoreContributions
            .Where(c => c.LeadId == leadId)
            .ToListAsync(cancellationToken);

        // Only the once-only contributions gate future awards. A repeatable rule is supposed to fire
        // again, so its past rows must not look like "already counted".
        var alreadyEarned = existing
            .Where(c => c.IsOnce)
            .Select(c => c.SourceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<LeadScoreContribution>();

        var fields = await _context.QualificationFields
            .Where(f => f.IsActive)
            .ToListAsync(cancellationToken);

        AwardFieldWeights(lead, known, alreadyEarned, added, now, fields);

        var hotRule = await AwardRulesAsync(lead, known, alreadyEarned, added, now, signals, cancellationToken);

        foreach (var contribution in added)
            _context.LeadScoreContributions.Add(contribution);

        var rawTotal = existing.Sum(c => c.Points) + added.Sum(c => c.Points);
        var score = Math.Clamp(rawTotal, MinScore, MaxScore);
        var band = BandFor(score);

        lead.ScoreNumeric = score;
        lead.Score = band;

        if (!string.IsNullOrWhiteSpace(signals.DetectedIntent))
            lead.CurrentIntent = signals.DetectedIntent;

        ApplyHotLead(lead, band, signals, hotRule, now);

        return new LeadScoreResult(
            score,
            rawTotal,
            band.ToString(),
            lead.HotLeadDetectedAt is not null,
            lead.HotLeadReason,
            added.Select(c => new LeadScoreContributionDto(
                StripPrefix(c.SourceKey), c.DisplayName, c.Points, c.AppliedAt)).ToList());
    }

    public static LeadScoreBand BandFor(int scoreNumeric) => scoreNumeric switch
    {
        >= HotBandThreshold => LeadScoreBand.Hot,
        >= WarmBandThreshold => LeadScoreBand.Warm,
        _ => LeadScoreBand.Cold
    };

    /// <summary>Awards each captured field's weight once. Runs over everything currently known rather
    /// than only what this turn captured, so a value that arrived by another route - a human typing it
    /// on the lead screen, or the backfill from the old Lead columns - earns its points the first time
    /// scoring runs afterwards, instead of being silently worth nothing forever.</summary>
    private static void AwardFieldWeights(
        Lead lead,
        IReadOnlyList<LeadQualificationValue> known,
        HashSet<string> alreadyEarned,
        List<LeadScoreContribution> added,
        DateTime now,
        IReadOnlyList<QualificationField> fields)
    {
        var byKey = fields.ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase);

        foreach (var value in known)
        {
            if (!byKey.TryGetValue(value.FieldKey, out var field) || field.ScoreWeight == 0)
                continue;

            var sourceKey = FieldPrefix + field.FieldKey;
            if (!alreadyEarned.Add(sourceKey))
                continue;

            added.Add(new LeadScoreContribution
            {
                TenantId = lead.TenantId,
                LeadId = lead.Id,
                RuleId = null,
                SourceKey = sourceKey,
                DisplayName = field.DisplayName,
                Points = field.ScoreWeight,
                IsOnce = true,
                AppliedAt = now
            });
        }
    }

    /// <summary>Evaluates every active rule, awarding the ones that match. Returns the first matching
    /// rule flagged MarksLeadHot, so the caller can say which signal made the lead hot rather than
    /// just that it is.</summary>
    private async Task<LeadScoringRule?> AwardRulesAsync(
        Lead lead,
        IReadOnlyList<LeadQualificationValue> known,
        HashSet<string> alreadyEarned,
        List<LeadScoreContribution> added,
        DateTime now,
        ScoringSignals signals,
        CancellationToken cancellationToken)
    {
        var rules = await _context.LeadScoringRules
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.RuleKey)
            .ToListAsync(cancellationToken);

        LeadScoringRule? hotRule = null;

        foreach (var rule in rules)
        {
            var sourceKey = RulePrefix + rule.RuleKey;

            if (rule.OncePerLead && alreadyEarned.Contains(sourceKey))
                continue;

            if (!Matches(rule, known, signals, now))
                continue;

            if (rule.OncePerLead)
                alreadyEarned.Add(sourceKey);

            added.Add(new LeadScoreContribution
            {
                TenantId = lead.TenantId,
                LeadId = lead.Id,
                RuleId = rule.Id,
                SourceKey = sourceKey,
                DisplayName = rule.DisplayName,
                Points = rule.Points,
                IsOnce = rule.OncePerLead,
                TriggeredByInteractionId = signals.AiInteractionId,
                AppliedAt = now
            });

            if (hotRule is null && rule.MarksLeadHot)
                hotRule = rule;
        }

        return hotRule;
    }

    private static bool Matches(
        LeadScoringRule rule, IReadOnlyList<LeadQualificationValue> known, ScoringSignals signals, DateTime now) =>
        rule.RuleType switch
        {
            LeadScoringRuleType.IntentMatch =>
                !string.IsNullOrWhiteSpace(signals.DetectedIntent)
                && string.Equals(signals.DetectedIntent, rule.MatchValue, StringComparison.OrdinalIgnoreCase),

            LeadScoringRuleType.FieldPresent =>
                known.Any(v => string.Equals(v.FieldKey, rule.MatchValue, StringComparison.OrdinalIgnoreCase)),

            LeadScoringRuleType.FieldValueMatch => MatchesFieldValue(rule.MatchValue, known),

            LeadScoringRuleType.TimelineWithinDays => MatchesTimeline(rule.MatchValue, known, now),

            LeadScoringRuleType.MessageKeyword => ContainsWord(signals.InboundMessageText, rule.MatchValue),

            LeadScoringRuleType.BuyingIntentDetected => signals.BuyingIntentDetected,

            _ => false
        };

    private static bool MatchesFieldValue(string matchValue, IReadOnlyList<LeadQualificationValue> known)
    {
        var parts = matchValue.Split('=', 2);
        if (parts.Length != 2)
            return false;

        var key = parts[0].Trim();
        var expected = parts[1].Trim();

        var value = known.FirstOrDefault(v => string.Equals(v.FieldKey, key, StringComparison.OrdinalIgnoreCase));
        if (value is null)
            return false;

        // Normalized first: "1 cr" and "10000000" are the same answer, and a rule written against
        // either spelling should match a customer who used the other.
        var actual = string.IsNullOrWhiteSpace(value.NormalizedValue) ? value.RawValue : value.NormalizedValue;
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fires only when the timeline actually normalized to a date. A customer who said "soon"
    /// has a raw value and no normalized one, and turning that into a day count would be inventing the
    /// precision this rule is supposed to test for.</summary>
    private static bool MatchesTimeline(string matchValue, IReadOnlyList<LeadQualificationValue> known, DateTime now)
    {
        if (!int.TryParse(matchValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days <= 0)
            return false;

        var value = known.FirstOrDefault(v =>
            string.Equals(v.FieldKey, QualificationDefaults.PurchaseTimelineKey, StringComparison.OrdinalIgnoreCase));

        if (value?.NormalizedValue is null)
            return false;

        if (!DateTime.TryParse(value.NormalizedValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var target))
            return false;

        var delta = target.Date - now.Date;
        return delta.TotalDays >= 0 && delta.TotalDays <= days;
    }

    /// <summary>Whole-word, so a rule on "buy" does not fire on "buying a house is hard" - or worse,
    /// on "budget".</summary>
    private static bool ContainsWord(string? text, string word)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(word))
            return false;

        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);

            if (beforeOk && afterOk)
                return true;

            index = afterIndex;
        }

        return false;
    }

    /// <summary>Sets the hot flag the first time any trigger fires, and never clears it. Going back to
    /// asking qualification questions after a customer has said they are ready is the behaviour the
    /// whole hot-lead concept exists to stop, so only a human clears this.</summary>
    private static void ApplyHotLead(
        Lead lead, LeadScoreBand band, ScoringSignals signals, LeadScoringRule? hotRule, DateTime now)
    {
        if (lead.HotLeadDetectedAt is not null)
            return;

        string? reason = null;

        if (hotRule is not null)
            reason = hotRule.DisplayName;
        else if (signals.BuyingIntentDetected)
            reason = "Customer showed buying intent";
        else if (Enum.TryParse<CustomerIntent>(signals.DetectedIntent, ignoreCase: true, out var intent)
                 && BuyingIntents.Contains(intent))
            reason = $"Intent: {intent}";
        else if (band == LeadScoreBand.Hot)
            reason = "Score reached the hot threshold";

        if (reason is null)
            return;

        lead.HotLeadDetectedAt = now;
        lead.HotLeadReason = reason;
    }

    /// <summary>Turns a stored SourceKey back into the plain field or rule key, for display. Public
    /// because the breakdown endpoint reads contributions straight out of the table and would
    /// otherwise show "rule:demo_requested" to a salesperson.</summary>
    public static string StripPrefix(string sourceKey) =>
        sourceKey.StartsWith(FieldPrefix, StringComparison.Ordinal) ? sourceKey[FieldPrefix.Length..]
        : sourceKey.StartsWith(RulePrefix, StringComparison.Ordinal) ? sourceKey[RulePrefix.Length..]
        : sourceKey;
}
