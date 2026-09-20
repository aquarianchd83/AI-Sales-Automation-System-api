using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Handoffs;

public class HandoffSummaryBuilder : IHandoffSummaryBuilder
{
    /// <summary>Enough recent contributions to explain the score without turning the card into a
    /// ledger. A lead with forty contributions has a scoring configuration problem, not a display one.</summary>
    private const int MaxScoreLines = 12;

    private const int MaxLastMessageChars = 500;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantConfigOverrideProvider _tenantConfig;

    public HandoffSummaryBuilder(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        ITenantConfigOverrideProvider tenantConfig)
    {
        _context = context;
        _dateTime = dateTime;
        _tenantConfig = tenantConfig;
    }

    public async Task<HandoffSummary> BuildAsync(
        Guid leadId, Guid conversationId, HandoffTurnContext turn, CancellationToken cancellationToken = default)
    {
        var options = await _tenantConfig.GetAiOptionsAsync(cancellationToken);

        var conversation = await _context.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        var customer = conversation is null
            ? null
            : await _context.Customers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == conversation.CustomerId, cancellationToken);

        var messages = await _context.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Direction, m.Text, m.CreatedAt })
            .Take(50)
            .ToListAsync(cancellationToken);

        var messageCount = await _context.Messages
            .CountAsync(m => m.ConversationId == conversationId, cancellationToken);

        var lastCustomerMessage = messages
            .FirstOrDefault(m => m.Direction == MessageDirection.Inbound)?.Text;

        var fields = await _context.QualificationFields
            .Where(f => f.IsActive)
            .OrderByDescending(f => f.IsRequired)
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.SortOrder)
            .ThenBy(f => f.FieldKey)
            .ToListAsync(cancellationToken);

        var values = await _context.LeadQualificationValues
            .Where(v => v.LeadId == leadId && !v.IsSuperseded)
            .ToListAsync(cancellationToken);

        // Same confidence floor the planner and scoring use: a value the model was unsure about is on
        // record but is not presented to an agent as something the customer told us.
        var known = values
            .Where(v => v.ExtractionConfidence >= options.MinFieldExtractionConfidence)
            .ToList();

        var displayNames = fields.ToDictionary(f => f.FieldKey, f => f.DisplayName, StringComparer.OrdinalIgnoreCase);
        var knownKeys = known.Select(v => v.FieldKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var qualification = known
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => new HandoffQualificationItem(
                displayNames.TryGetValue(v.FieldKey, out var name) ? name : v.FieldKey,
                v.RawValue,
                v.CapturedByUserId is not null,
                v.CreatedAt))
            .ToList();

        var stillUnknown = fields
            .Where(f => !knownKeys.Contains(f.FieldKey))
            .Select(f => f.DisplayName)
            .ToList();

        var scoreBreakdown = await BuildScoreBreakdownAsync(leadId, turn.NewScoreLines, cancellationToken);

        // Customer.FullName is empty, not null, when neither name was ever captured - which is the
        // common case for a WhatsApp contact who just messaged in. The number is what an agent would
        // recognise anyway, and beats heading the card "Unknown customer" for someone we do know.
        var customerName = customer is null ? "Unknown customer"
            : !string.IsNullOrWhiteSpace(customer.FullName) ? customer.FullName
            : customer.PhoneNumberE164;

        return new HandoffSummary(
            CustomerName: customerName,
            CustomerPhoneNumberE164: customer?.PhoneNumberE164 ?? string.Empty,
            Requirement: conversation?.Summary,
            Qualification: qualification,
            StillUnknown: stillUnknown,
            ScoreNumeric: turn.ScoreNumeric,
            Temperature: turn.ScoreBand,
            IsHot: turn.IsHot,
            HotReason: turn.HotReason,
            ScoreBreakdown: scoreBreakdown,
            DetectedIntent: turn.DetectedIntent,
            TriggerReason: turn.TriggerReason.ToString(),
            AgentNote: turn.AgentNote,
            BlockedReason: turn.BlockedReason,
            LastCustomerMessage: Truncate(lastCustomerMessage, MaxLastMessageChars),
            MessageCount: messageCount,
            ConversationStartedAt: conversation?.CreatedAt ?? _dateTime.UtcNow,
            EscalatedAt: _dateTime.UtcNow);
    }

    /// <summary>
    /// Merges the contributions already committed with the ones this turn just staged.
    ///
    /// The merge is the point: at escalation the current turn's contributions exist only on the change
    /// tracker, and a breakdown that omitted them would be missing precisely the signal that caused
    /// the escalation - the demo request whose points made the lead hot would not appear in the card
    /// explaining why it is hot.
    /// </summary>
    private async Task<IReadOnlyList<HandoffScoreLine>> BuildScoreBreakdownAsync(
        Guid leadId, IReadOnlyList<HandoffScoreLine> newLines, CancellationToken cancellationToken)
    {
        var persisted = await _context.LeadScoreContributions
            .Where(c => c.LeadId == leadId)
            .OrderByDescending(c => c.AppliedAt)
            .Take(MaxScoreLines)
            .Select(c => new { c.DisplayName, c.Points })
            .ToListAsync(cancellationToken);

        return newLines
            .Concat(persisted.Select(c => new HandoffScoreLine(c.DisplayName, c.Points)))
            .Take(MaxScoreLines)
            .ToList();
    }

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null
        : text.Length <= max ? text.Trim()
        : text.Trim()[..max] + "...";
}
