using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>
/// Builds the agent performance report from what the orchestrator already records.
///
/// <para>The per-field numbers are computed in memory rather than in SQL, and that is a deliberate
/// trade rather than an oversight. "Did a value arrive AFTER we asked for it" needs the turns of a
/// conversation in order, and the captured keys live in a JSON array - both of which SQL Server can do
/// and neither of which it can do legibly. The window's turns are pulled once, as six columns, and
/// walked. At a few tens of thousands of turns that is a fine report query; past that this wants a
/// rollup table, and the window cap below is what keeps it honest in the meantime.</para>
/// </summary>
public class AgentPerformanceService : IAgentPerformanceService
{
    /// <summary>Long enough to see a trend, short enough that the in-memory pass above stays a report
    /// query rather than a table scan someone runs by accident.</summary>
    private const int MaxDays = 90;

    private const int DefaultDays = 30;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;

    public AgentPerformanceService(IApplicationDbContext context, IDateTimeProvider dateTime)
    {
        _context = context;
        _dateTime = dateTime;
    }

    public async Task<AgentPerformanceReportDto> GetReportAsync(int days, CancellationToken cancellationToken = default)
    {
        var window = days <= 0 ? DefaultDays : Math.Min(days, MaxDays);
        var toUtc = _dateTime.UtcNow;
        var fromUtc = toUtc.AddDays(-window);

        var turns = await _context.AiInteractions
            .Where(i => i.CreatedAt >= fromUtc && i.CreatedAt <= toUtc)
            .OrderBy(i => i.ConversationId)
            .ThenBy(i => i.CreatedAt)
            .Select(i => new TurnRow(
                i.ConversationId,
                i.ActionTaken,
                i.ConfidenceScore,
                i.LatencyMs,
                i.PromptTokens,
                i.CompletionTokens,
                i.AskedFieldKey,
                i.CapturedFieldKeysJson))
            .ToListAsync(cancellationToken);

        var blockedReplies = await _context.AiInteractionValidationFailures
            .Where(f => f.CreatedAt >= fromUtc && f.CreatedAt <= toUtc && f.Blocking)
            .Select(f => f.AiInteractionId)
            .Distinct()
            .CountAsync(cancellationToken);

        // Every configured field, not just the active ones: a field switched off last week still has
        // a fortnight of history in this window, and hiding it would make the drop look like a
        // regression rather than a decision someone made.
        var fields = await _context.QualificationFields
            .OrderByDescending(f => f.IsRequired)
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.SortOrder)
            .ThenBy(f => f.FieldKey)
            .Select(f => new FieldRow(f.FieldKey, f.DisplayName, f.IsRequired, f.IsActive))
            .ToListAsync(cancellationToken);

        return new AgentPerformanceReportDto(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Turns: BuildTurnStats(turns, blockedReplies),
            Fields: BuildFieldStats(turns, fields),
            ValidationFailures: await BuildValidationStatsAsync(fromUtc, toUtc, turns.Count, cancellationToken),
            Leads: await BuildLeadStatsAsync(fromUtc, toUtc, cancellationToken));
    }

    private static AgentTurnStatsDto BuildTurnStats(IReadOnlyList<TurnRow> turns, int blockedReplies)
    {
        if (turns.Count == 0)
            return new AgentTurnStatsDto(0, 0, 0, 0, 0, 0, 0, 0, 0);

        var replied = turns.Count(t => t.ActionTaken == AiActionTaken.Replied);

        return new AgentTurnStatsDto(
            TotalTurns: turns.Count,
            RepliedTurns: replied,
            EscalatedTurns: turns.Count(t => t.ActionTaken == AiActionTaken.Escalated),
            ContainmentRate: Rate(replied, turns.Count),
            AverageConfidence: Math.Round(turns.Average(t => t.ConfidenceScore), 3),
            BlockedReplies: blockedReplies,
            AverageLatencyMs: (int)Math.Round(turns.Average(t => t.LatencyMs)),
            PromptTokens: turns.Sum(t => (long)(t.PromptTokens ?? 0)),
            CompletionTokens: turns.Sum(t => (long)(t.CompletionTokens ?? 0)));
    }

    /// <summary>
    /// One ordered pass per conversation, recording for each field when it was first asked and when a
    /// value first arrived. "Captured after an ask" is the pair in that order - a value the customer
    /// volunteered in turn one is a capture, but it says nothing about whether the question works.
    /// </summary>
    private static IReadOnlyList<QualificationFieldStatsDto> BuildFieldStats(
        IReadOnlyList<TurnRow> turns, IReadOnlyList<FieldRow> fields)
    {
        var asks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var conversationsAsked = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var conversationsCaptured = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var conversationsCapturedAfterAsk = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var turnsToCapture = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var conversation in turns.GroupBy(t => t.ConversationId))
        {
            // Already ordered by the query; grouping preserves it.
            var ordered = conversation.ToList();
            var firstAskAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < ordered.Count; index++)
            {
                var turn = ordered[index];

                if (!string.IsNullOrWhiteSpace(turn.AskedFieldKey))
                {
                    var key = turn.AskedFieldKey;
                    Increment(asks, key);
                    Add(conversationsAsked, key, conversation.Key);

                    if (!firstAskAt.ContainsKey(key))
                        firstAskAt[key] = index;
                }

                foreach (var captured in ParseCapturedKeys(turn.CapturedFieldKeysJson))
                {
                    // First capture only: a value re-stated later is the same answer, and counting it
                    // again would make a chatty customer look like a well-phrased question.
                    if (!Add(conversationsCaptured, captured, conversation.Key))
                        continue;

                    if (firstAskAt.TryGetValue(captured, out var askedAt))
                    {
                        Add(conversationsCapturedAfterAsk, captured, conversation.Key);

                        if (!turnsToCapture.TryGetValue(captured, out var list))
                            turnsToCapture[captured] = list = new List<int>();

                        list.Add(index - askedAt + 1);
                    }
                }
            }
        }

        return fields
            .Select(f =>
            {
                var asked = Count(conversationsAsked, f.FieldKey);
                var capturedAfterAsk = Count(conversationsCapturedAfterAsk, f.FieldKey);

                return new QualificationFieldStatsDto(
                    FieldKey: f.FieldKey,
                    DisplayName: f.DisplayName,
                    IsRequired: f.IsRequired,
                    IsActive: f.IsActive,
                    TimesAsked: asks.TryGetValue(f.FieldKey, out var timesAsked) ? timesAsked : 0,
                    ConversationsAsked: asked,
                    ConversationsCaptured: Count(conversationsCaptured, f.FieldKey),
                    ConversationsCapturedAfterAsk: capturedAfterAsk,
                    AskEffectiveness: Rate(capturedAfterAsk, asked),
                    AverageTurnsToCapture: turnsToCapture.TryGetValue(f.FieldKey, out var spans) && spans.Count > 0
                        ? Math.Round(spans.Average(), 2)
                        : null);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<ValidationFailureStatsDto>> BuildValidationStatsAsync(
        DateTime fromUtc, DateTime toUtc, int totalTurns, CancellationToken cancellationToken)
    {
        // Joined to the turn so "affected conversations" is a count of conversations rather than of
        // failure rows - ten leaks in one conversation is one conversation with a problem.
        var rows = await (
            from failure in _context.AiInteractionValidationFailures
            where failure.CreatedAt >= fromUtc && failure.CreatedAt <= toUtc
            join interaction in _context.AiInteractions on failure.AiInteractionId equals interaction.Id
            select new { failure.Code, failure.Blocking, interaction.ConversationId })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ValidationFailureStatsDto(
                Code: g.Key,
                // A check is blocking or it is not; grouped rows only disagree across a build where
                // that changed, and the newer reading is the one worth showing.
                Blocking: g.Any(r => r.Blocking),
                Occurrences: g.Count(),
                AffectedConversations: g.Select(r => r.ConversationId).Distinct().Count(),
                ShareOfTurns: Rate(g.Count(), totalTurns)))
            .OrderByDescending(s => s.Occurrences)
            .ThenBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<LeadOutcomeStatsDto> BuildLeadStatsAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var leads = await _context.Leads
            .Where(l => l.CreatedAt >= fromUtc && l.CreatedAt <= toUtc)
            .Select(l => new { l.Score, l.Stage, l.HotLeadDetectedAt })
            .ToListAsync(cancellationToken);

        var hotDetected = leads.Count(l => l.HotLeadDetectedAt != null);
        var hotWon = leads.Count(l => l.HotLeadDetectedAt != null && l.Stage == LeadStage.Won);
        var otherLeads = leads.Count - hotDetected;
        var otherWon = leads.Count(l => l.HotLeadDetectedAt == null && l.Stage == LeadStage.Won);

        return new LeadOutcomeStatsDto(
            TotalLeads: leads.Count,
            Hot: leads.Count(l => l.Score == LeadScoreBand.Hot),
            Warm: leads.Count(l => l.Score == LeadScoreBand.Warm),
            Cold: leads.Count(l => l.Score == LeadScoreBand.Cold),
            HotLeadsDetected: hotDetected,
            HotLeadsWon: hotWon,
            HotConversionRate: Rate(hotWon, hotDetected),
            OtherLeadsWon: otherWon,
            OtherConversionRate: Rate(otherWon, otherLeads));
    }

    /// <summary>Empty, malformed or absent JSON reads as "captured nothing". A report that threw on
    /// one bad row would be unreadable for the month that row is in the window.</summary>
    private static IReadOnlyList<string> ParseCapturedKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) is { Count: > 0 } keys
                ? keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList()
                : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : Math.Round((double)numerator / denominator, 4);

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;

    /// <summary>True when this conversation was not already in the set - i.e. this is the first time.</summary>
    private static bool Add(Dictionary<string, HashSet<Guid>> sets, string key, Guid conversationId)
    {
        if (!sets.TryGetValue(key, out var set))
            sets[key] = set = new HashSet<Guid>();

        return set.Add(conversationId);
    }

    private static int Count(Dictionary<string, HashSet<Guid>> sets, string key) =>
        sets.TryGetValue(key, out var set) ? set.Count : 0;

    private record FieldRow(string FieldKey, string DisplayName, bool IsRequired, bool IsActive);

    private record TurnRow(
        Guid ConversationId,
        AiActionTaken ActionTaken,
        double ConfidenceScore,
        int LatencyMs,
        int? PromptTokens,
        int? CompletionTokens,
        string? AskedFieldKey,
        string? CapturedFieldKeysJson);
}
