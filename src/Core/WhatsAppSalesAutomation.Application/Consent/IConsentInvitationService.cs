using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// Obtains WhatsApp consent: an SMS invites the customer to message the business first, and the customer's own inbound WhatsApp
/// message - never the SMS, never a click - is what records the opt-in. Tracks the SMS (pending, submitted, delivered, failed),
/// takes the provider's delivery receipts, and starts the campaign's first message once consent is on record.
/// </summary>
public interface IConsentInvitationService
{
    /// <summary>
    /// Sends the invitation to each customer in <paramref name="customerIds"/> who needs one, subject to the pre-send checks. Safe to
    /// call again for the same customers: one invitation exists per (tenant, customer, campaign), so a retry or a second run never
    /// sends twice. Every outcome - sent, failed, skipped and why - is written to the customer's history.
    /// </summary>
    Task<ConsentInvitationBatchResult> SendAsync(
        Guid campaignId, Guid? executionId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken = default);

    /// <summary>Tries Failed invitations again once their backoff has passed and while their attempt budget lasts. Returns how many were tried.</summary>
    Task<int> RetryDueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the SMS provider's delivery receipt for <paramref name="providerMessageId"/>. Idempotent: a repeated receipt, or one that
    /// would move an invitation backwards, changes nothing and writes no second history entry. Returns false when nothing changed
    /// (a duplicate, or an id this system never sent).
    /// </summary>
    Task<bool> ApplyReceiptAsync(
        string providerMessageId, ConsentInvitationStatus status, DateTime atUtc, string? reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called for an inbound WhatsApp message from <paramref name="customer"/>. When the customer has an invitation that is still waiting for
    /// an answer, records their opt-in with the message as evidence and starts the campaign's first message. A message from a customer with
    /// no open invitation changes nothing here. Called at most once per inbound message (the webhook processor drops duplicates first), and
    /// an answered invitation is never answered twice.
    /// </summary>
    Task<ConsentReplyOutcome> HandleInboundMessageAsync(
        Customer customer, InboundConsentMessage message, CancellationToken cancellationToken = default);
}

/// <summary>The inbound WhatsApp message being considered as an answer to an invitation.</summary>
public record InboundConsentMessage(string WhatsAppMessageId, string? Text, DateTime TimestampUtc);

/// <param name="Sent">Invitations handed to the provider in this call.</param>
/// <param name="Failed">Invitations the provider rejected (retried within the attempt budget).</param>
/// <param name="Skipped">Customers who needed none or were not allowed one, each with its reason in <paramref name="Details"/>.</param>
public record ConsentInvitationBatchResult(int Sent, int Failed, int Skipped, IReadOnlyList<string> Details)
{
    public static ConsentInvitationBatchResult Empty { get; } = new(0, 0, 0, Array.Empty<string>());
}

/// <param name="InvitationAnswered">The message answered an open invitation.</param>
/// <param name="ConsentRecorded">Consent was recorded by this message (false if the customer was already opted in).</param>
/// <param name="FirstMessage">What happened to the campaign's first message, when one was attempted.</param>
public record ConsentReplyOutcome(bool InvitationAnswered, bool ConsentRecorded, FirstMessageOutcome? FirstMessage)
{
    public static ConsentReplyOutcome None { get; } = new(false, false, null);
}
