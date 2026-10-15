using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Checks whether mobile numbers may be used for outreach (DND and similar restrictions). A batch goes in, and one result
/// per customer comes back, matched by <see cref="PhoneVerificationItem.ReferenceId"/> (the discovery row, since a rejected number never becomes a customer) - never by order or by number. The
/// simulator is the only implementation today; a real provider replaces it behind this contract without changing the workflow,
/// and aligns the field names and status codes to its own API in its adapter.
/// </summary>
public interface IPhoneVerificationProvider
{
    Task<PhoneVerificationResponse> VerifyAsync(
        Guid tenantId, IReadOnlyList<PhoneVerificationItem> items, CancellationToken cancellationToken = default);
}

public record PhoneVerificationItem(Guid ReferenceId, string PhoneE164);

/// <param name="RequestId">The provider's reference for the whole batch, kept for troubleshooting.</param>
public record PhoneVerificationResponse(string RequestId, IReadOnlyList<PhoneVerificationItemResult> Results);

/// <param name="Status">Pass, Fail, Pending or Error.</param>
public record PhoneVerificationItemResult(
    Guid ReferenceId, LeadDiscoveryVerificationStatus Status, string? ReasonCode, string? Reason);
