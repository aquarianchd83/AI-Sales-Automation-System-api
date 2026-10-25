using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Customers;

/// <summary>
/// One step in a customer's processing timeline - discovered, verified, added to a campaign, messaged, opted in.
/// Append-only from the application's point of view: a correction is written as a further event, never by
/// editing or deleting an earlier one, so the timeline stays usable as evidence of what was done with a
/// paid-for customer. Verification, WhatsApp consent and campaign processing are separate dimensions; an event
/// records the transition of whichever one it concerns in <see cref="PreviousStatus"/>/<see cref="NewStatus"/>.
/// </summary>
public class CustomerHistoryEvent : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>One of <see cref="CustomerHistoryEventTypes"/>. A string rather than an enum so later phases add
    /// event kinds without a schema change.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>When the thing happened, which is not necessarily when the row was written.</summary>
    public DateTime OccurredAtUtc { get; set; }

    public string? PreviousStatus { get; set; }

    public string? NewStatus { get; set; }

    /// <summary>The outcome or reason, in words the tenant can read.</summary>
    public string? Reason { get; set; }

    /// <summary>The Lead Discovery execution (batch) this event belongs to, if any.</summary>
    public Guid? ExecutionId { get; set; }

    public Guid? CampaignId { get; set; }

    /// <summary>Ties the events of one processing pass together for troubleshooting.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>An external provider's reference for the event (verification request id, SMS id, WhatsApp message id).</summary>
    public string? ProviderReference { get; set; }
}

/// <summary>The event names a <see cref="CustomerHistoryEvent"/> uses.</summary>
public static class CustomerHistoryEventTypes
{
    public const string Discovered = "Discovered";
    public const string VerificationSubmitted = "VerificationSubmitted";
    public const string VerificationPassed = "VerificationPassed";
    public const string VerificationFailed = "VerificationFailed";
    public const string VerificationError = "VerificationError";
    public const string VerificationPending = "VerificationPending";

    // Consent invitation (SMS) and the customer's WhatsApp reply.
    public const string SmsQueued = "SmsQueued";
    public const string SmsSubmitted = "SmsSubmitted";
    public const string SmsDelivered = "SmsDelivered";
    public const string SmsFailed = "SmsFailed";
    public const string SmsSkipped = "SmsSkipped";
    public const string ConversationStarted = "ConversationStarted";
    public const string ConsentRecorded = "ConsentRecorded";

    /// <summary>The customer chose "No, Not Interested" on the consent page. Not a failure; no WhatsApp conversation follows.</summary>
    public const string ConsentDeclined = "ConsentDeclined";

    // The campaign's first message after consent.
    public const string FirstMessageSent = "FirstMessageSent";
    public const string FirstMessageFailed = "FirstMessageFailed";
    public const string WaitingForContent = "WaitingForContent";
    public const string ContentAvailable = "ContentAvailable";

    /// <summary>Where an event falls among others recorded at the same instant - the order the process runs in: discovered, verified, invited
    /// by SMS, the customer's reply and consent, then the campaign's first message.</summary>
    public static int Rank(string eventType) => eventType switch
    {
        Discovered => 0,
        VerificationSubmitted => 1,
        VerificationPassed or VerificationFailed or VerificationError or VerificationPending => 2,
        SmsQueued => 3,
        SmsSkipped => 3,
        SmsSubmitted => 4,
        SmsDelivered or SmsFailed => 5,
        ConversationStarted => 6,
        ConsentRecorded or ConsentDeclined => 7,
        WaitingForContent => 8,
        ContentAvailable => 9,
        FirstMessageSent or FirstMessageFailed => 10,
        _ => 50
    };

    /// <summary>The event that records a verification outcome.</summary>
    public static string ForVerification(LeadDiscoveryVerificationStatus status) => status switch
    {
        LeadDiscoveryVerificationStatus.Pass => VerificationPassed,
        LeadDiscoveryVerificationStatus.Fail => VerificationFailed,
        LeadDiscoveryVerificationStatus.Pending => VerificationPending,
        _ => VerificationError
    };
}
