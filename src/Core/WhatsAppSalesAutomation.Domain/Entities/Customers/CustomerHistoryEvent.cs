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

    /// <summary>Where an event falls among others recorded at the same instant: discovered, then submitted, then any outcome.</summary>
    public static int Rank(string eventType) => eventType switch
    {
        Discovered => 0,
        VerificationSubmitted => 1,
        _ => 2
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
