using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Handoffs;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>
/// Implements architecture doc &sect;8's state machine. Called once per non-opt-out inbound message,
/// after InboundWebhookProcessor has already persisted the Message and opened/reused the Conversation.
///
/// Two deliberate simplifications versus the doc's pseudocode, both flagged rather than silently
/// applied: (1) Mode == AI never sends an "optional holding message" on escalation - the doc marks it
/// optional and a holding message adds a second outbound send to reason about for no functional gain
/// yet. (2) Mode == Hybrid is currently handled identically to Mode == AI (full escalate-or-reply, no
/// partial "answer the FAQ-safe part first") - splitting one AI turn into a partial answer plus a
/// handoff needs product-defined rules for what counts as "FAQ-safe" that do not exist yet; Hybrid mode
/// still functions, it just does not yet get the more nuanced behaviour the doc describes for it.
/// </summary>
public class ConversationOrchestrator : IConversationOrchestrator
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IAiService _ai;
    private readonly IKnowledgeBaseService _knowledgeBase;
    private readonly ILeadService _leads;
    private readonly IHandoffService _handoffs;
    private readonly IWhatsAppService _whatsApp;
    private readonly INotificationService _notifications;
    private readonly AiOptions _options;
    private readonly ILogger<ConversationOrchestrator> _logger;

    public ConversationOrchestrator(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IAiService ai,
        IKnowledgeBaseService knowledgeBase,
        ILeadService leads,
        IHandoffService handoffs,
        IWhatsAppService whatsApp,
        INotificationService notifications,
        IOptions<AiOptions> options,
        ILogger<ConversationOrchestrator> logger)
    {
        _context = context;
        _dateTime = dateTime;
        _ai = ai;
        _knowledgeBase = knowledgeBase;
        _leads = leads;
        _handoffs = handoffs;
        _whatsApp = whatsApp;
        _notifications = notifications;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleInboundMessageAsync(Guid conversationId, Guid customerId, Guid inboundMessageId, CancellationToken cancellationToken = default)
    {
        var conversation = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        var customer = await _context.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        var inboundMessage = await _context.Messages.FirstOrDefaultAsync(m => m.Id == inboundMessageId, cancellationToken);

        // Defensive only - InboundWebhookProcessor just created all three rows in the same unit of
        // work immediately before calling this, so a miss here would mean something else deleted them
        // in between, not a normal code path.
        if (conversation is null || customer is null || inboundMessage is null)
        {
            _logger.LogWarning(
                "ConversationOrchestrator could not load Conversation/Customer/Message for {ConversationId}/{CustomerId}/{MessageId} - skipping.",
                conversationId, customerId, inboundMessageId);
            return;
        }

        // "no AI action, notify assigned agent via SignalR" (architecture doc §8) - the agent
        // notification itself already happened via InboundWebhookProcessor's unconditional
        // NotifyNewInboundMessageAsync call, so there is nothing left to do here. No AiInteraction row
        // is written for a Human-mode turn - see that entity's own doc comment for why.
        if (conversation.Mode == ConversationMode.Human)
            return;

        var retrieved = await _knowledgeBase.RetrieveRelevantChunksAsync(inboundMessage.Text ?? string.Empty, cancellationToken);
        var groundingChunks = retrieved.Select(r => new AiKnowledgeSnippet(r.ChunkId, r.Text, r.RelevanceScore)).ToList();

        var historyRows = await _context.Messages
            .Where(m => m.ConversationId == conversationId && m.Id != inboundMessageId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(_options.ConversationHistoryTurns)
            .ToListAsync(cancellationToken);
        historyRows.Reverse(); // oldest first, for a natural reading order in the prompt

        var context = new AiConversationContext(
            conversationId,
            customer.FullName,
            inboundMessage.Text ?? string.Empty,
            historyRows.Select(m => new AiConversationTurn(m.Direction, m.Text ?? string.Empty, m.CreatedAt)).ToList(),
            groundingChunks,
            conversation.Summary);

        var result = await _ai.GetResponseAsync(context, cancellationToken);

        var escalate = result.ConfidenceScore < _options.ConfidenceThreshold ||
            _options.EscalationIntents.Any(i => string.Equals(i, result.DetectedIntent, StringComparison.OrdinalIgnoreCase));

        var interaction = new AiInteraction
        {
            ConversationId = conversationId,
            InboundMessageId = inboundMessageId,
            DetectedIntent = result.DetectedIntent,
            ConfidenceScore = result.ConfidenceScore,
            ExtractedEntitiesJson = JsonSerializer.Serialize(result.ExtractedEntities),
            ProposedResponseText = result.ResponseText,
            ActionTaken = escalate ? AiActionTaken.Escalated : AiActionTaken.Replied,
            ModelUsed = result.ModelUsed,
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            LatencyMs = result.LatencyMs
        };
        _context.AiInteractions.Add(interaction);

        foreach (var citedChunkId in result.CitedChunkIds)
        {
            var snippet = groundingChunks.FirstOrDefault(g => g.ChunkId == citedChunkId);
            if (snippet is null)
                continue; // defensive - a provider should only cite ids it was actually given

            _context.AiInteractionSources.Add(new AiInteractionSource
            {
                AiInteractionId = interaction.Id,
                KnowledgeBaseChunkId = citedChunkId,
                RelevanceScore = snippet.RelevanceScore
            });
        }

        conversation.AiConfidenceLast = result.ConfidenceScore;
        conversation.LastDetectedIntent = result.DetectedIntent;
        conversation.Summary = result.UpdatedSummary;

        // CampaignId null - this Lead originates from an inbound conversation, not a campaign. A
        // customer who already has a Lead from a campaign keeps using that same non-terminal Lead
        // (GetOrCreateActiveLeadIdAsync's own rule), so this does not fork a second Lead for someone
        // who first came in via a campaign and is now just replying to it.
        var leadId = await _leads.GetOrCreateActiveLeadIdAsync(customerId, campaignId: null, cancellationToken);
        var lead = await _leads.ApplyAiExtractedAttributesAsync(leadId, result.ExtractedEntities, result.DetectedIntent, cancellationToken);
        conversation.LastLeadScore = Enum.Parse<LeadScoreBand>(lead.Score, ignoreCase: true);

        if (escalate)
        {
            conversation.Status = ConversationStatus.Escalated;

            var triggerReason = MapIntentToTriggerReason(result.DetectedIntent);
            var notes = $"AI escalation - intent '{result.DetectedIntent ?? "none"}', confidence {result.ConfidenceScore:P0}.";
            var handoff = await _handoffs.GetOrCreateOpenHandoffAsync(conversationId, triggerReason.ToString(), notes, cancellationToken);

            await _notifications.NotifyNewHandoffAsync(handoff.Id, conversationId, handoff.TriggerReason, cancellationToken);
        }
        else
        {
            await SendAiReplyAsync(conversation, customer, inboundMessageId, result.ResponseText, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Sends the AI's reply exactly like ConversationService.SendMessageAsync's free-text path
    /// (manually-built Message + IWhatsAppService.SendTextMessageAsync), not by calling that method
    /// directly - it re-validates a DTO and re-checks the customer service window, both pointless here
    /// since InboundWebhookProcessor just set LastInboundMessageAt to now, so the window is trivially
    /// open. If the send itself fails, that is treated as independent of the AI's decision to reply
    /// (AiInteraction.ActionTaken stays Replied - the AI's job was done correctly) and a Handoff is
    /// raised anyway, since the customer is still owed a response that never arrived.
    /// </summary>
    private async Task SendAiReplyAsync(Conversation conversation, Customer customer, Guid inboundMessageId, string responseText, CancellationToken cancellationToken)
    {
        var message = new Message
        {
            CustomerId = customer.Id,
            ConversationId = conversation.Id,
            Direction = MessageDirection.Outbound,
            MessageType = MessageType.Text,
            Text = responseText,
            // Deterministic per inbound message: a retry of this same orchestrator run (should one
            // ever happen) reuses the same key rather than risking a double-send.
            IdempotencyKey = $"ai:{inboundMessageId}",
            Status = MessageStatus.Queued
        };
        _context.Messages.Add(message);

        var result = await _whatsApp.SendTextMessageAsync(customer.PhoneNumberE164, responseText, cancellationToken);
        message.AttemptCount++;

        if (result.Success)
        {
            message.Status = MessageStatus.Sent;
            message.WhatsAppMessageId = result.WhatsAppMessageId;
            message.SentAt = _dateTime.UtcNow;
            conversation.LastMessageAt = _dateTime.UtcNow;
        }
        else
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = result.ErrorMessage;

            conversation.Status = ConversationStatus.Escalated;
            var handoff = await _handoffs.GetOrCreateOpenHandoffAsync(
                conversation.Id,
                nameof(HandoffTriggerReason.RuleTriggered),
                $"AI reply failed to send: {result.ErrorMessage}",
                cancellationToken);

            await _notifications.NotifyNewHandoffAsync(handoff.Id, conversation.Id, handoff.TriggerReason, cancellationToken);
        }
    }

    private static HandoffTriggerReason MapIntentToTriggerReason(string? detectedIntent) => detectedIntent?.ToLowerInvariant() switch
    {
        "complaint" => HandoffTriggerReason.Complaint,
        "negotiation" => HandoffTriggerReason.Negotiation,
        "complextechnical" => HandoffTriggerReason.ComplexTechnical,
        "humanrequest" => HandoffTriggerReason.CustomerRequested,
        _ => HandoffTriggerReason.LowConfidence
    };
}
