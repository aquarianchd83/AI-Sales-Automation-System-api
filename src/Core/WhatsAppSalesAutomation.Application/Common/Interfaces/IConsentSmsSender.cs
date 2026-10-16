using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Sends the SMS that invites a customer to start a WhatsApp conversation. A provider abstraction like the verification one: the
/// simulator is the only implementation today, and a real SMS provider replaces it behind this contract without changing the workflow.
/// Acceptance is not delivery: <see cref="ConsentSmsSendResult.Accepted"/> only says the provider took the request, and delivery is
/// reported later by a receipt (see <c>IConsentInvitationService.ApplyReceiptAsync</c>).
/// </summary>
public interface IConsentSmsSender
{
    /// <param name="request">Carries an idempotency key; a provider that supports one must not send twice for the same key.</param>
    Task<ConsentSmsSendResult> SendAsync(ConsentSmsRequest request, CancellationToken cancellationToken = default);
}

public record ConsentSmsRequest(string ToPhoneE164, string Body, string IdempotencyKey);

/// <param name="Accepted">The provider accepted the request.</param>
/// <param name="ProviderMessageId">What a later delivery receipt refers to.</param>
/// <param name="FailureReason">Why it was rejected, in words the tenant can read.</param>
/// <param name="Transient">A rejection worth retrying (a provider outage), as opposed to a permanent one (an unreachable number).</param>
/// <param name="ImmediateReceipt">Only a provider that reports the outcome in the same call (the simulator) sets this; a real,
/// asynchronous provider leaves it null and the receipt arrives later.</param>
public record ConsentSmsSendResult(
    bool Accepted,
    string? ProviderMessageId,
    string? FailureReason,
    bool Transient = false,
    ConsentSmsReceipt? ImmediateReceipt = null);

/// <param name="Status">Delivered or Failed.</param>
public record ConsentSmsReceipt(ConsentInvitationStatus Status, string? Reason = null);
