using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Customers;

/// <summary>
/// One SMS carrying a link to the tenant's WhatsApp consent page, which is how consent is obtained: the customer chooses Yes or No there.
/// (Older rows invited the customer to message the business first.)
/// One row per (tenant, customer, campaign), so a retry or a second run never sends the invitation twice - the unique
/// <see cref="IdempotencyKey"/> is the final guarantee. The SMS and a click on its link are never consent; only the customer's explicit
/// "Yes, I Agree" on the consent page is, and the decision, the wording and the page version are recorded here as the evidence.
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

    // ---- The consent page: the SMS carries a link to it, and the customer's explicit choice there is the consent. ----

    /// <summary>SHA-256 (hex) of the secret in the link. The raw token is never stored. Null on a legacy wa.me invitation.</summary>
    public string? TokenHash { get; set; }

    public DateTime? TokenExpiresAtUtc { get; set; }

    /// <summary>The customer's decision - separate from <see cref="Status"/>, which is only about the SMS.</summary>
    public ConsentDecisionStatus DecisionStatus { get; set; } = ConsentDecisionStatus.Pending;

    /// <summary>Created when the consent page was not ready, so no SMS went out. Sent later by the retry sweep, without a second discovery charge.</summary>
    public bool HeldForConsentPage { get; set; }

    public DateTime? DecisionAtUtc { get; set; }

    /// <summary>Where the decision came from, e.g. "ConsentPage".</summary>
    public string? DecisionSource { get; set; }

    /// <summary>The <see cref="ConsentPageVersion"/> the customer saw when deciding.</summary>
    public int? DecisionPageVersion { get; set; }

    /// <summary>The exact consent sentence agreed to (a copy, so it survives any later change).</summary>
    public string? DecisionConsentText { get; set; }

    /// <summary>SHA-256 of the client IP, for audit without storing the address itself.</summary>
    public string? DecisionIpHash { get; set; }

    public string? DecisionUserAgent { get; set; }

    public WhatsAppInitiationStatus WhatsAppInitiation { get; set; } = WhatsAppInitiationStatus.NotStarted;

    public DateTime? WhatsAppInitiatedAtUtc { get; set; }

    public int WhatsAppAttemptCount { get; set; }

    public string? WhatsAppInitiationError { get; set; }

    /// <summary>Optimistic-concurrency guard: two racing decisions (or two workers starting the conversation) cannot both win.</summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
