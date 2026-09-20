using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Handoffs;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Application.Leads;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Constants;
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
    private readonly IHandoffSummaryBuilder _handoffSummary;
    private readonly IWhatsAppService _whatsApp;
    private readonly INotificationService _notifications;
    private readonly ITenantConfigOverrideProvider _tenantConfig;
    private readonly IQuotaGate _quota;
    private readonly IQualificationPlanner _qualification;
    private readonly ILeadScoringService _scoring;
    private readonly IAiReplyValidator _validator;
    private readonly ILogger<ConversationOrchestrator> _logger;

    public ConversationOrchestrator(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IAiService ai,
        IKnowledgeBaseService knowledgeBase,
        ILeadService leads,
        IHandoffService handoffs,
        IHandoffSummaryBuilder handoffSummary,
        IWhatsAppService whatsApp,
        INotificationService notifications,
        ITenantConfigOverrideProvider tenantConfig,
        IQuotaGate quota,
        IQualificationPlanner qualification,
        ILeadScoringService scoring,
        IAiReplyValidator validator,
        ILogger<ConversationOrchestrator> logger)
    {
        _quota = quota;
        _context = context;
        _dateTime = dateTime;
        _ai = ai;
        _knowledgeBase = knowledgeBase;
        _leads = leads;
        _handoffs = handoffs;
        _handoffSummary = handoffSummary;
        _whatsApp = whatsApp;
        _notifications = notifications;
        _tenantConfig = tenantConfig;
        _qualification = qualification;
        _scoring = scoring;
        _validator = validator;
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

        // Resolved per call (not once per DI scope) - merges this tenant's Ai:* overrides, if any,
        // over the platform default. See ITenantConfigOverrideProvider's own doc comment. The ambient
        // tenant is already set correctly by the time this runs - see InboundWebhookProcessor's own
        // doc comment.
        var options = await _tenantConfig.GetAiOptionsAsync(cancellationToken);

        var historyRows = await _context.Messages
            .Where(m => m.ConversationId == conversationId && m.Id != inboundMessageId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(options.ConversationHistoryTurns)
            .ToListAsync(cancellationToken);
        historyRows.Reverse(); // oldest first, for a natural reading order in the prompt

        // The lead is resolved before the AI call now, not after: the qualification plan cannot be
        // built without it, and the plan is what the prompt is shaped around. A side effect is that a
        // lead now exists even when the AI never runs (no quota, provider down) - which is the more
        // honest record anyway: someone who messaged us is a lead whether or not our model was up.
        var leadId = await _leads.GetOrCreateActiveLeadIdAsync(customerId, campaignId: null, cancellationToken);
        var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken);

        var business = await BuildBusinessProfileAsync(conversation.TenantId, cancellationToken);

        // Hot leads stop being asked questions. Enforced by withholding the questions from the prompt
        // rather than by instructing the model not to use them.
        var qualificationPaused = lead?.HotLeadDetectedAt is not null;
        var plan = await _qualification.PlanAsync(leadId, qualificationPaused, cancellationToken);

        var context = new AiConversationContext(
            conversationId,
            customer.FullName,
            inboundMessage.Text ?? string.Empty,
            historyRows.Select(m => new AiConversationTurn(m.Direction, m.Text ?? string.Empty, m.CreatedAt)).ToList(),
            groundingChunks,
            conversation.Summary,
            business,
            plan.SchemaFields,
            plan.Known,
            plan.ToAsk,
            qualificationPaused,
            customer.PreferredLanguage);

        // Prepaid: one AI conversation is spent before the model is called (the provider bills whether or not the
        // AI ends up replying or escalating). With none left the customer is handed to a human instead of being
        // left unanswered. Keyed per inbound message, so a retry of this same run never spends twice.
        if (!await _quota.TryConsumeAiConversationAsync(conversation.TenantId, $"ai-conv:{inboundMessageId}", inboundMessageId.ToString(), cancellationToken))
        {
            conversation.Status = ConversationStatus.Escalated;
            var noQuotaHandoff = await _handoffs.GetOrCreateOpenHandoffAsync(
                conversationId,
                nameof(HandoffTriggerReason.RuleTriggered),
                "AI conversation quota used up - the tenant needs to buy credits or wait for renewal.",
                cancellationToken: cancellationToken);
            await _notifications.NotifyNewHandoffAsync(noQuotaHandoff.Id, conversationId, noQuotaHandoff.TriggerReason, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        var result = await _ai.GetResponseAsync(context, cancellationToken);

        // Nothing the model returned is trusted yet. Everything below works from the validated view.
        var validated = _validator.Validate(result, context);

        if (validated.HasFailures)
        {
            _logger.LogWarning(
                "AI reply validation for conversation {ConversationId} raised: {Failures}",
                conversationId, validated.FailureSummary);
        }

        // Opt-out is handled before anything else the turn might do. A customer who asked us to stop
        // is owed that first, and a promotional reply sent alongside the acknowledgement would be the
        // exact thing they asked us not to do.
        if (validated.OptOutRequested && customer.OptInStatus != OptInStatus.OptedOut)
        {
            customer.OptInStatus = OptInStatus.OptedOut;
            customer.OptOutTimestamp = _dateTime.UtcNow;
            customer.OptOutSource = OptOutSource.AiDetected;

            _logger.LogInformation(
                "AI detected an opt-out from customer {CustomerId} on conversation {ConversationId}",
                customerId, conversationId);
        }

        var accepted = await _qualification.CaptureAsync(
            leadId, inboundMessageId, validated.ExtractedFields, cancellationToken);

        var score = await _scoring.RecomputeAsync(
            leadId,
            new ScoringSignals(
                validated.DetectedIntent,
                validated.BuyingIntentDetected,
                inboundMessage.Text,
                AiInteractionId: null),
            cancellationToken);

        // Escalation is decided here, from validated signals and computed state - never from the
        // model's own sense of whether it did well.
        var escalate =
            validated.ConfidenceScore < options.ConfidenceThreshold
            || options.EscalationIntents.Any(i => string.Equals(i, validated.DetectedIntent, StringComparison.OrdinalIgnoreCase))
            || validated.HumanRequested
            || !validated.CanSend
            || (score.IsHot && options.HandoffOnHotLead);

        var interaction = new AiInteraction
        {
            ConversationId = conversationId,
            InboundMessageId = inboundMessageId,
            DetectedIntent = validated.DetectedIntent,
            ConfidenceScore = validated.ConfidenceScore,
            // The accepted fields, not what the model claimed - an audit of what was believed is
            // more useful than an audit of what was asserted.
            ExtractedEntitiesJson = JsonSerializer.Serialize(accepted),
            ProposedResponseText = validated.ResponseText,
            ActionTaken = escalate ? AiActionTaken.Escalated : AiActionTaken.Replied,
            ModelUsed = result.ModelUsed,
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            LatencyMs = result.LatencyMs,
            CapturedFieldKeysJson = JsonSerializer.Serialize(accepted.Select(a => a.FieldKey)),
            AskedFieldKey = validated.AskedFieldKey,
            BuyingIntentReported = validated.BuyingIntentDetected,
            HumanRequestReported = validated.HumanRequested,
            OptOutReported = validated.OptOutRequested
        };
        _context.AiInteractions.Add(interaction);

        foreach (var citedChunkId in validated.CitedChunkIds)
        {
            var snippet = groundingChunks.FirstOrDefault(g => g.ChunkId == citedChunkId);
            if (snippet is null)
                continue; // defensive - the validator already dropped ids this turn was not given

            _context.AiInteractionSources.Add(new AiInteractionSource
            {
                AiInteractionId = interaction.Id,
                KnowledgeBaseChunkId = citedChunkId,
                RelevanceScore = snippet.RelevanceScore
            });
        }

        conversation.AiConfidenceLast = validated.ConfidenceScore;
        conversation.LastDetectedIntent = validated.DetectedIntent;
        conversation.Summary = validated.UpdatedSummary;
        if (Enum.TryParse<LeadScoreBand>(score.Band, ignoreCase: true, out var parsedBand))
            conversation.LastLeadScore = parsedBand;

        UpdatePreferredLanguage(customer, validated.DetectedLanguage, historyRows.Count);

        if (escalate)
        {
            conversation.Status = ConversationStatus.Escalated;

            var triggerReason = PickTriggerReason(validated, score);

            // Built before the handoff row, and from this turn's own numbers: score.NewContributions
            // are still on the change tracker at this point, so the builder is handed them rather than
            // left to query a table that does not yet contain them.
            var summary = await _handoffSummary.BuildAsync(
                leadId,
                conversationId,
                new HandoffTurnContext(
                    triggerReason,
                    validated.DetectedIntent,
                    validated.AgentNote,
                    // Only set when validation is what stopped the reply. An escalation on low
                    // confidence or on an escalation intent is not a blocked reply, and saying so
                    // would send the agent looking for a fault that is not there.
                    validated.CanSend ? null : validated.FailureSummary,
                    score.ScoreNumeric,
                    score.Band,
                    score.IsHot,
                    score.HotReason,
                    score.NewContributions.Select(c => new HandoffScoreLine(c.DisplayName, c.Points)).ToList()),
                cancellationToken);

            var handoff = await _handoffs.GetOrCreateOpenHandoffAsync(
                conversationId,
                triggerReason.ToString(),
                BuildHandoffNote(validated, score, plan),
                summary,
                cancellationToken);

            await _notifications.NotifyNewHandoffAsync(handoff.Id, conversationId, handoff.TriggerReason, cancellationToken);
        }
        else if (validated.OptOutRequested)
        {
            // Nothing promotional goes to someone who just asked us to stop. The opt-out itself was
            // already applied above; staying silent is the whole point.
            _logger.LogInformation(
                "Suppressed AI reply on conversation {ConversationId} because the customer opted out.",
                conversationId);
        }
        else
        {
            await SendAiReplyAsync(conversation, customer, inboundMessageId, validated.ResponseText, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The tenant's business as the prompt needs it. Read per turn rather than cached: it
    /// changes rarely, but a tenant who has just corrected their working hours should not have to wait
    /// for a cache to expire before the agent stops quoting the old ones.</summary>
    private async Task<AiBusinessProfile> BuildBusinessProfileAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        return tenant is null
            // Defensive only: a conversation always has a tenant. A bare profile keeps the agent
            // answering from the knowledge base rather than failing the turn outright.
            ? new AiBusinessProfile("the business", null, null, null, null, null, null, ConversationGoal.Enquiry, false)
            : new AiBusinessProfile(
                tenant.Name,
                tenant.Industry,
                tenant.BusinessLocation,
                tenant.WebsiteUrl,
                tenant.WorkingHours,
                tenant.ProductName,
                tenant.BusinessDescription,
                tenant.AiConversationGoal,
                tenant.AiMayDiscloseLeadScore);
    }

    /// <summary>
    /// Updates the customer's saved language only once they have used the same one across more than a
    /// single turn.
    ///
    /// Updating on one turn would be wrong in the common case: someone who usually writes Hindi and
    /// replies "ok" once would have every future message pinned to English.
    /// </summary>
    private static void UpdatePreferredLanguage(Customer customer, string? detected, int historyCount)
    {
        if (string.IsNullOrWhiteSpace(detected) || historyCount == 0)
            return;

        if (string.Equals(customer.PreferredLanguage, detected, StringComparison.OrdinalIgnoreCase))
            return;

        // Only set it when there was nothing there. Changing an established preference needs more
        // evidence than one turn, and the prompt already tells the agent to match the latest message
        // regardless of what is stored - so the stored value is a hint, not the thing that decides.
        if (string.IsNullOrWhiteSpace(customer.PreferredLanguage))
            customer.PreferredLanguage = detected;
    }

    /// <summary>The one-line version, for the queue list where there is room for a sentence and not a
    /// card. The full briefing goes to <see cref="HandoffSummary"/>; this stays because a list row that
    /// reads "AI escalation" and nothing else makes an agent open every item to triage any of them.</summary>
    private static string BuildHandoffNote(ValidatedReply validated, LeadScoreResult score, QualificationPlan plan)
    {
        var parts = new List<string>
        {
            $"intent '{validated.DetectedIntent}'",
            $"confidence {validated.ConfidenceScore:P0}",
            $"score {score.ScoreNumeric} ({score.Band})",
            $"qualified {plan.CapturedCount}/{plan.TotalCount}"
        };

        if (score.IsHot && !string.IsNullOrWhiteSpace(score.HotReason))
            parts.Add($"HOT - {score.HotReason}");

        if (validated.HumanRequested)
            parts.Add("customer asked for a person");

        if (!validated.CanSend)
            parts.Add($"reply blocked ({validated.FailureSummary})");

        if (!string.IsNullOrWhiteSpace(validated.AgentNote))
            parts.Add(validated.AgentNote);

        return "AI escalation - " + string.Join("; ", parts);
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
                cancellationToken: cancellationToken);

            await _notifications.NotifyNewHandoffAsync(handoff.Id, conversation.Id, handoff.TriggerReason, cancellationToken);
        }
    }

    /// <summary>Why a human is being brought in, in the order that matters to whoever reads the queue:
    /// an explicit request first, then a customer ready to buy, then the intent, then the catch-all.</summary>
    private static HandoffTriggerReason PickTriggerReason(ValidatedReply validated, LeadScoreResult score)
    {
        if (validated.HumanRequested)
            return HandoffTriggerReason.CustomerRequested;

        if (score.IsHot)
            return HandoffTriggerReason.HotLead;

        if (!validated.CanSend)
            return HandoffTriggerReason.CannotAnswer;

        return Enum.TryParse<CustomerIntent>(validated.DetectedIntent, ignoreCase: true, out var intent)
            ? intent switch
            {
                CustomerIntent.Complaint => HandoffTriggerReason.Complaint,
                CustomerIntent.HumanRequest => HandoffTriggerReason.CustomerRequested,
                CustomerIntent.Negotiation => HandoffTriggerReason.Negotiation,
                CustomerIntent.Support => HandoffTriggerReason.ComplexTechnical,
                CustomerIntent.PurchaseIntent or CustomerIntent.Booking or CustomerIntent.DemoRequest
                    or CustomerIntent.AppointmentRequest or CustomerIntent.SiteVisit
                    => HandoffTriggerReason.HotLead,
                _ => HandoffTriggerReason.LowConfidence
            }
            : HandoffTriggerReason.LowConfidence;
    }
}
