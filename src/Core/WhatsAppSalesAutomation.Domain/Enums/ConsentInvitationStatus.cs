namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// Where an SMS invitation to chat on WhatsApp stands. Submitted means the SMS provider accepted the request - not that the
/// customer's phone received it; only a delivery receipt moves it to Delivered. Whether the customer then replied is a separate
/// fact (<c>ConsentInvitation.ConsentRecordedAtUtc</c>), because an SMS or a click alone is never consent.
/// </summary>
public enum ConsentInvitationStatus
{
    /// <summary>Recorded and reserved, not yet handed to the provider (or waiting for a retry).</summary>
    Pending = 0,

    /// <summary>Accepted by the SMS provider; no delivery receipt yet.</summary>
    Submitted = 1,

    /// <summary>The provider reported it reached the customer's phone.</summary>
    Delivered = 2,

    /// <summary>Rejected, or reported undelivered; see the failure reason. Retried within the attempt budget.</summary>
    Failed = 3
}
