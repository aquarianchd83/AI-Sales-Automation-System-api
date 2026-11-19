using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Payments;

/// <summary>
/// One Razorpay subscription a tenant started - which, for UPI AutoPay, is also the record of the mandate: Razorpay does not expose the
/// mandate as an object of its own. Unlike <c>Subscription</c> (one row per tenant, "what access does this tenant have right now") this
/// is a history: an abandoned authorisation, a cancelled mandate and its replacement are each a row, and none is ever deleted.
/// <see cref="Status"/> is Razorpay's own state, moved only by a signed webhook or by what Razorpay's API reports - never by the browser.
/// Nothing here can debit anyone: no UPI id, PIN or bank detail is stored. Amounts are in paise.
/// </summary>
public class RazorpaySubscription : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Our plan (<c>Plan.Id</c>) this mandate pays for.</summary>
    public Guid PlanId { get; set; }

    /// <summary>The <see cref="RazorpayPlan"/> row it was created against.</summary>
    public Guid RazorpayPlanRowId { get; set; }

    /// <summary>Razorpay's subscription id, e.g. "sub_Nx1...".</summary>
    public string RazorpaySubscriptionId { get; set; } = string.Empty;

    /// <summary>"test" or "live" - which keys created it.</summary>
    public string Mode { get; set; } = "test";

    public RazorpaySubscriptionStatus Status { get; set; } = RazorpaySubscriptionStatus.Created;

    /// <summary>The recurring amount the tenant agreed to, tax included - a snapshot, so a later price change never rewrites it.</summary>
    public long AmountMinor { get; set; }

    public string Currency { get; set; } = "INR";

    /// <summary>Razorpay's hosted page where the tenant authorises the mandate.</summary>
    public string? ShortUrl { get; set; }

    /// <summary>How many cycles the mandate was created for.</summary>
    public int TotalCount { get; set; }

    /// <summary>Cycles Razorpay reports as paid.</summary>
    public int PaidCount { get; set; }

    /// <summary>The billing cycle Razorpay reports as current. The end is the tenant's paid-through date.</summary>
    public DateTime? CurrentStartUtc { get; set; }

    public DateTime? CurrentEndUtc { get; set; }

    /// <summary>When Razorpay will next try to debit.</summary>
    public DateTime? ChargeAtUtc { get; set; }

    public DateTime? AuthenticatedAtUtc { get; set; }

    public DateTime? ActivatedAtUtc { get; set; }

    public DateTime? CancelledAtUtc { get; set; }

    /// <summary>When it stopped for good: cancelled, completed or expired.</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>The tenant asked to stop renewing; access continues to <see cref="CurrentEndUtc"/>.</summary>
    public bool CancelAtCycleEnd { get; set; }

    /// <summary>upi, card, emandate ... as Razorpay reports it.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>Razorpay's customer id (cust_...), set once the mandate is authorised.</summary>
    public string? CustomerId { get; set; }

    /// <summary>Razorpay's own timestamp of the newest event applied. Webhooks arrive in any order, so an older event must not undo a newer one.</summary>
    public DateTime? LastEventAtUtc { get; set; }

    public string? LastEvent { get; set; }

    /// <summary>When this row was last compared with Razorpay's record by the reconciliation job.</summary>
    public DateTime? LastReconciledAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

/// <summary>Razorpay's subscription states, one for one.</summary>
public enum RazorpaySubscriptionStatus
{
    /// <summary>Created, the mandate not yet authorised.</summary>
    Created = 0,

    /// <summary>The mandate is authorised; the first cycle has not started.</summary>
    Authenticated = 1,

    Active = 2,

    /// <summary>A debit failed and Razorpay is retrying it.</summary>
    Pending = 3,

    /// <summary>Every retry failed. Nothing more is debited until the tenant acts.</summary>
    Halted = 4,

    /// <summary>Final: a cancelled subscription can never be restarted.</summary>
    Cancelled = 5,

    /// <summary>Every cycle it was created for has been charged.</summary>
    Completed = 6,

    /// <summary>The mandate was never authorised in time.</summary>
    Expired = 7,

    Paused = 8
}
