namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from the "Billing:AutoPay" config section. The limits recurring billing through Razorpay UPI AutoPay works within. Whether AutoPay
/// is on at all is not here: that is the <c>Razorpay:AutoPayEnabled</c> setting, kept with the Razorpay keys so the operator can switch it
/// without a deploy.
/// </summary>
public class AutoPayOptions
{
    /// <summary>The most one recurring debit may be, in rupees, tax included. UPI AutoPay allows 15,000 per debit for most merchant
    /// categories; a plan that costs more cannot be sold through it and is refused before anything is created at Razorpay.</summary>
    public decimal MaxRecurringAmountInr { get; set; } = 15_000m;

    /// <summary>How many monthly cycles a mandate is created for. Razorpay requires a count; 120 is ten years, the longest its
    /// documentation agrees on.</summary>
    public int TotalCount { get; set; } = 120;

    /// <summary>How long the tenant has to authorise a mandate before Razorpay expires it. 0 leaves it to Razorpay.</summary>
    public int AuthorizationExpiryHours { get; set; } = 48;

    /// <summary>How many times applying one stored webhook event is tried before it is left as Failed for a person to look at. The waits
    /// between tries grow (5 minutes, 10, 20 ... capped at 6 hours), so the default covers about a day.</summary>
    public int WebhookMaxAttempts { get; set; } = 10;

    /// <summary>A stored event nothing has picked up after this many minutes is taken by the retry pass - the job that should have
    /// applied it was lost (a restart, the queue being down).</summary>
    public int WebhookPickupMinutes { get; set; } = 2;

    /// <summary>A mandate nobody authorised is cancelled at Razorpay this many hours after it was started, when Razorpay has not
    /// already expired it. Longer than <see cref="AuthorizationExpiryHours"/> on purpose: Razorpay gets to say so first.</summary>
    public int AbandonedAuthorizationHours { get; set; } = 72;
}
