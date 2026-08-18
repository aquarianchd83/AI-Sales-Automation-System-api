using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Handoffs;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Webhooks;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Webhooks;

public class InboundWebhookProcessor : IInboundWebhookProcessor
{
    /// <summary>Case-insensitive, exact-match (after trim) - deliberately not a substring match, so
    /// "please stop calling me" is not mistaken for an opt-out.</summary>
    private static readonly HashSet<string> OptOutKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "unsubscribe", "unsub", "cancel", "opt out", "optout", "quit"
    };

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IWhatsAppWebhookParser _parser;
    private readonly IConversationService _conversations;
    private readonly IHandoffService _handoffs;
    private readonly INotificationService _notifications;
    private readonly ILogger<InboundWebhookProcessor> _logger;

    public InboundWebhookProcessor(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IWhatsAppWebhookParser parser,
        IConversationService conversations,
        IHandoffService handoffs,
        INotificationService notifications,
        ILogger<InboundWebhookProcessor> logger)
    {
        _context = context;
        _dateTime = dateTime;
        _parser = parser;
        _conversations = conversations;
        _handoffs = handoffs;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<Guid> RecordAsync(string eventType, string rawPayload, CancellationToken cancellationToken = default)
    {
        var webhookEvent = new WebhookEvent
        {
            Provider = "WhatsApp",
            EventType = eventType,
            RawPayload = rawPayload,
            ReceivedAt = _dateTime.UtcNow
        };

        _context.WebhookEvents.Add(webhookEvent);
        await _context.SaveChangesAsync(cancellationToken);

        return webhookEvent.Id;
    }

    public async Task ProcessAsync(Guid webhookEventId, CancellationToken cancellationToken = default)
    {
        var webhookEvent = await _context.WebhookEvents.FirstOrDefaultAsync(w => w.Id == webhookEventId, cancellationToken);
        if (webhookEvent is null)
        {
            _logger.LogWarning("WebhookEvent {Id} not found when processing was attempted", webhookEventId);
            return;
        }

        // Any failure here - malformed payload, an unexpected data shape - is caught and recorded on
        // the WebhookEvent itself rather than left to Hangfire's own automatic retry: those are
        // payload-shape problems retrying will not fix, and this guarantees the row always ends in a
        // terminal, inspectable state (Processed/Duplicate/Failed) instead of looping or vanishing.
        try
        {
            var parsed = _parser.Parse(webhookEvent.RawPayload);
            var processedAnything = false;

            foreach (var status in parsed.Statuses)
                processedAnything |= await ApplyStatusUpdateAsync(status, cancellationToken);

            foreach (var message in parsed.Messages)
                processedAnything |= await ProcessInboundMessageAsync(message, cancellationToken);

            webhookEvent.WhatsAppMessageId ??= parsed.Messages.FirstOrDefault()?.WhatsAppMessageId
                ?? parsed.Statuses.FirstOrDefault()?.WhatsAppMessageId;
            webhookEvent.ProcessingStatus = processedAnything ? WebhookProcessingStatus.Processed : WebhookProcessingStatus.Duplicate;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process WebhookEvent {Id}", webhookEventId);
            webhookEvent.ProcessingStatus = WebhookProcessingStatus.Failed;
            webhookEvent.ProcessingError = ex.Message;
        }

        webhookEvent.ProcessedAt = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Returns false for a status referring to a message we have no record of (nothing to
    /// update) or one that would move the message backwards (a stale/out-of-order delivery).</summary>
    private async Task<bool> ApplyStatusUpdateAsync(WhatsAppStatusUpdate status, CancellationToken cancellationToken)
    {
        var message = await _context.Messages.FirstOrDefaultAsync(m => m.WhatsAppMessageId == status.WhatsAppMessageId, cancellationToken);
        if (message is null)
            return false;

        var newStatus = status.Status.ToLowerInvariant() switch
        {
            "sent" => MessageStatus.Sent,
            "delivered" => MessageStatus.Delivered,
            "read" => MessageStatus.Read,
            "failed" => MessageStatus.Failed,
            _ => (MessageStatus?)null
        };

        if (newStatus is null || !CanAdvanceTo(message.Status, newStatus.Value))
            return false;

        message.Status = newStatus.Value;
        if (newStatus == MessageStatus.Delivered)
            message.DeliveredAt ??= status.Timestamp;
        if (newStatus == MessageStatus.Read)
            message.ReadAt ??= status.Timestamp;

        return true;
    }

    /// <summary>
    /// Explicit transition table rather than a numeric Ord comparison: MessageStatus's own int values
    /// put Failed=4 after Read=3, which would let a stale "failed" event wrongly regress an
    /// already-Read message if compared as plain numbers.
    /// </summary>
    private static bool CanAdvanceTo(MessageStatus current, MessageStatus incoming)
    {
        if (current == MessageStatus.Read)
            return false;

        return incoming switch
        {
            MessageStatus.Sent => current == MessageStatus.Queued,
            MessageStatus.Delivered => current is MessageStatus.Queued or MessageStatus.Sent,
            MessageStatus.Read => current is MessageStatus.Queued or MessageStatus.Sent or MessageStatus.Delivered,
            MessageStatus.Failed => current is MessageStatus.Queued or MessageStatus.Sent,
            _ => false
        };
    }

    private async Task<bool> ProcessInboundMessageAsync(InboundWhatsAppMessage inbound, CancellationToken cancellationToken)
    {
        var alreadyRecorded = await _context.Messages.AnyAsync(m => m.WhatsAppMessageId == inbound.WhatsAppMessageId, cancellationToken);
        if (alreadyRecorded)
            return false;

        if (!PhoneNumberNormalizer.TryNormalize(inbound.FromPhone, out var phoneNumber))
        {
            // Meta always sends digits shaped like a real number here - this should not happen in
            // practice, but falling back to a '+'-prefixed raw value keeps the message from being
            // silently dropped over a normalization edge case.
            phoneNumber = inbound.FromPhone.StartsWith('+') ? inbound.FromPhone : $"+{inbound.FromPhone}";
        }

        var customer = await _context.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.PhoneNumberE164 == phoneNumber, cancellationToken);

        if (customer is null)
        {
            // Auto-created because someone messaging us is real, actionable inbound contact an agent
            // needs to see - but this does NOT opt them into marketing. PendingOptIn stays the
            // default; being able to reply to an inbound message and being allowed to receive a
            // campaign are two separate permissions in this system, same as everywhere else.
            customer = new Customer
            {
                PhoneNumberE164 = phoneNumber,
                FirstName = inbound.ContactName,
                Source = "Inbound",
                OptInStatus = OptInStatus.PendingOptIn
            };
            _context.Customers.Add(customer);
            await _context.SaveChangesAsync(cancellationToken);
        }
        else if (customer.IsDeleted)
        {
            // Reactivate rather than create a second row for the same number - the unique index on
            // PhoneNumberE164 covers soft-deleted rows too (see CustomerService's own reasoning).
            customer.IsDeleted = false;
            customer.DeletedAt = null;
        }

        var isOptOut = !string.IsNullOrWhiteSpace(inbound.TextBody) && OptOutKeywords.Contains(inbound.TextBody.Trim());
        if (isOptOut && customer.OptInStatus != OptInStatus.OptedOut)
        {
            customer.OptInStatus = OptInStatus.OptedOut;
            customer.OptOutTimestamp = _dateTime.UtcNow;
        }

        var conversationId = await _conversations.GetOrCreateActiveConversationIdAsync(customer.Id, cancellationToken);
        var conversation = await _context.Conversations.FirstAsync(c => c.Id == conversationId, cancellationToken);
        conversation.LastMessageAt = inbound.Timestamp;
        conversation.LastInboundMessageAt = inbound.Timestamp;

        var message = new Message
        {
            CustomerId = customer.Id,
            ConversationId = conversationId,
            Direction = MessageDirection.Inbound,
            MessageType = string.Equals(inbound.MessageType, "text", StringComparison.OrdinalIgnoreCase)
                ? MessageType.Text
                : MessageType.Interactive,
            Text = inbound.TextBody,
            WhatsAppMessageId = inbound.WhatsAppMessageId,
            // Kept populated for every Message row regardless of direction (see Message.IdempotencyKey),
            // even though WhatsAppMessageId's own unique index already guards inbound dedup on its own.
            IdempotencyKey = $"inbound:{inbound.WhatsAppMessageId}",
            // MessageStatus was designed for the outbound send lifecycle (Queued -> ... -> Read) and
            // has no dedicated "received" value for inbound - Delivered is the closest honest fit
            // ("this reached its destination", which for inbound is us), not Read, which for outbound
            // specifically means the customer read our message - an unrelated fact this enum does not
            // track for inbound at all. SentAt is reused the same way: the customer's own send time.
            Status = MessageStatus.Delivered,
            DeliveredAt = inbound.Timestamp,
            SentAt = inbound.Timestamp
        };
        _context.Messages.Add(message);

        if (isOptOut)
        {
            // Opting out is itself the resolution - Phase 1's rule is "stop all automation, log,
            // notify agent (no AI reply)", not "escalate", so no Handoff is raised for a pure STOP.
            await _context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            conversation.Status = ConversationStatus.Escalated;
            await _context.SaveChangesAsync(cancellationToken);

            // No IAiService exists until Phase 5, so there is nothing that could even attempt a reply
            // first - every non-opt-out inbound message needs a human, unconditionally, regardless of
            // Conversation.Mode. GetOrCreateOpenHandoffAsync means a chatty customer's second and third
            // message do not each spawn a new queue entry while the first is still open.
            var handoff = await _handoffs.GetOrCreateOpenHandoffAsync(
                conversationId,
                nameof(HandoffTriggerReason.RuleTriggered),
                "AI is not available until Phase 5 - every inbound message currently requires a human.",
                cancellationToken);

            await _notifications.NotifyNewHandoffAsync(handoff.Id, conversationId, handoff.TriggerReason, cancellationToken);
        }

        await _notifications.NotifyNewInboundMessageAsync(conversationId, customer.Id, inbound.TextBody, cancellationToken);

        return true;
    }
}
