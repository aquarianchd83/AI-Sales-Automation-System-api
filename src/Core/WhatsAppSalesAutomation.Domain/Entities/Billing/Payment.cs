using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One payment a tenant has made for a plan - unlike <see cref="Subscription"/> (one row per
/// tenant, "what does this tenant's access look like right now"), this is an append-only history:
/// every plan purchase/switch adds a new row, never updates an old one. Right now every row is
/// simulated (<see cref="Provider"/> is always "Simulated" - see BillingService.ChoosePlanAsync,
/// the interim stand-in for a real payment gateway while Stripe is on hold and Razorpay isn't wired
/// in yet for this platform's India-first launch); <see cref="PlanName"/>/<see cref="AmountCents"/>/
/// <see cref="CurrencyCode"/>/<see cref="CurrencySymbol"/>/<see cref="LocalAmount"/> are all
/// snapshotted at payment time rather than joined live, so a later plan rename/retire or the
/// tenant's own Country change never rewrites history.
/// </summary>
public class Payment : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public PaymentKind Kind { get; set; } = PaymentKind.Subscription;

    /// <summary>Null for a credit-pack purchase, which is not tied to a plan.</summary>
    public Guid? PlanId { get; set; }

    public Guid? CreditPackId { get; set; }

    /// <summary>For a Refund row: the payment it returns money for. AmountCents/LocalAmount are negative.</summary>
    public Guid? RefundOfPaymentId { get; set; }

    public string PlanName { get; set; } = string.Empty;

    /// <summary>Base USD price at payment time - mirrors Plan.PriceMonthlyCents, the same figure
    /// GetPlansAsync/ChoosePlanAsync convert from.</summary>
    public int AmountCents { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public string CurrencySymbol { get; set; } = string.Empty;

    /// <summary>AmountCents converted to the tenant's own currency at payment time - see
    /// RegionalPricingCatalog.Resolve, the same conversion GetPlansAsync already does for display.</summary>
    public decimal LocalAmount { get; set; }

    /// <summary>The tenant's country and state when this was charged - a snapshot, so a later change of either never
    /// rewrites history. Null on rows from before country-wise pricing.</summary>
    public string? CountryCode { get; set; }

    public string? StateCode { get; set; }

    /// <summary>Tax added on top of <see cref="LocalAmount"/> (the price before tax), in the tenant's currency.</summary>
    public decimal TaxLocal { get; set; }

    /// <summary>The individual tax lines as JSON - [{Name, RatePercent, Amount}] - so an invoice can show CGST and SGST apart.</summary>
    public string? TaxLinesJson { get; set; }

    /// <summary>What the tenant actually paid: <see cref="LocalAmount"/> plus <see cref="TaxLocal"/>. Zero on rows from before
    /// tax was recorded - read those through <see cref="TotalPaidLocal"/>.</summary>
    public decimal TotalLocal { get; set; }

    /// <summary>The total in rupees at the exchange rate of the day, for the admin's reports - stored, so a later rate
    /// change never moves a past figure.</summary>
    public decimal AmountInr { get; set; }

    /// <summary>INR per one unit of <see cref="CurrencyCode"/> when this was charged.</summary>
    public decimal FxRateToInr { get; set; }

    /// <summary>The billing period a subscription payment covers.</summary>
    public DateTime? PeriodStartUtc { get; set; }

    public DateTime? PeriodEndUtc { get; set; }

    public decimal TotalPaidLocal => TotalLocal != 0 ? TotalLocal : LocalAmount;

    /// <summary>"Simulated" today; becomes "Razorpay" (or whatever's next) once a real payment
    /// gateway is wired in - this column is what future code branches on, not a schema change.</summary>
    public string Provider { get; set; } = "Simulated";

    public DateTime PaidAtUtc { get; set; }
}
