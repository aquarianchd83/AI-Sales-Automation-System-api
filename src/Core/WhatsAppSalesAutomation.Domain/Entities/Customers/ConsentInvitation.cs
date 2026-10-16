using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Customers;

/// <summary>
/// One SMS asking a customer to start a WhatsApp conversation, which is how consent is obtained: the customer must message the
/// business first. One row per (tenant, customer, campaign), so a retry or a second run never sends the invitation twice - the
/// unique <see cref="IdempotencyKey"/> is the final guarantee. The SMS and a click on its link are never consent; the customer's own
/// inbound WhatsApp message is, and that message is recorded here as the evidence.
/// </summary>
public class ConsentInvitation : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>The campaign the customer was added to, whose first message follows once consent is recorded.</summary>
    public Guid? CampaignId { get; set; }

    /// <summary>The Lead Discovery execution (batch) the customer came from.</summary>
    public Guid? ExecutionId { get; set; }

    /// <summary>"{TenantId}:{CustomerId}:{CampaignId}" - unique.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public ConsentInvitationStatus Status { get; set; } = ConsentInvitationStatus.Pending;

    /// <summary>The number the SMS goes to, in E.164.</summary>
    public string ToPhoneE164 { get; set; } = string.Empty;

    /// <summary>The text sent, with the business name and the wa.me link filled in.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>The wa.me link inside <see cref="Body"/>, built from the tenant's WhatsApp number.</summary>
    public string WhatsAppLink { get; set; } = string.Empty;

    /// <summary>The SMS provider's id for the message; what a delivery receipt refers to.</summary>
    public string? ProviderMessageId { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>When a Failed invitation may be tried again; null once it will not be.</summary>
    public DateTime? NextAttemptAt { get; set; }

    public string? FailureReason { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    // ---- Consent evidence, set when the customer's own WhatsApp message answers the invitation. ----

    public DateTime? ConsentRecordedAtUtc { get; set; }

    /// <summary>WhatsApp's id of the inbound message that counted as consent.</summary>
    public string? ConsentMessageId { get; set; }

    /// <summary>Ties the events of one invitation together for troubleshooting.</summary>
    public string? CorrelationId { get; set; }
}
