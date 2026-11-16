using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Payments;

/// <summary>
/// The Razorpay plan that stands for one of our plans at one exact recurring charge. A Razorpay plan can never be edited or deleted and
/// carries a single fixed amount, while what a tenant pays depends on the country's price and the tax on top - so there is one row per
/// (plan, mode, currency, amount, period, interval), created at Razorpay the first time that charge is needed and reused ever after.
/// A price change simply produces a new row; mandates already agreed keep the plan they were authorised against.
/// Platform-global, like <c>Plan</c>. Amounts are in the currency's smallest unit (paise for INR), exactly as Razorpay counts them.
/// </summary>
public class RazorpayPlan : BaseEntity
{
    /// <summary>Our plan (<c>Plan.Id</c>).</summary>
    public Guid PlanId { get; set; }

    /// <summary>"test" or "live" - which keys created it. A test plan id means nothing to the live account.</summary>
    public string Mode { get; set; } = "test";

    public string Currency { get; set; } = "INR";

    /// <summary>What is debited each cycle, tax included.</summary>
    public long AmountMinor { get; set; }

    /// <summary>Razorpay's billing period: daily, weekly, monthly, quarterly or yearly.</summary>
    public string Period { get; set; } = "monthly";

    /// <summary>How many periods make one cycle (1 = every month).</summary>
    public int Interval { get; set; } = 1;

    /// <summary>Razorpay's plan id, e.g. "plan_Nx1...".</summary>
    public string RazorpayPlanId { get; set; } = string.Empty;
}
