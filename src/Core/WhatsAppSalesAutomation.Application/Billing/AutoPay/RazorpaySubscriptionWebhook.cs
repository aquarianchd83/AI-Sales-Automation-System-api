using System.Text.Json;
using WhatsAppSalesAutomation.Application.Razorpay;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>The payment a subscription event carries, when a debit was attempted before the event was raised.</summary>
/// <param name="Amount">In paise.</param>
/// <param name="Status">created, authorized, captured, refunded or failed.</param>
/// <param name="InvoiceId">Razorpay's invoice for the billing cycle (inv_...).</param>
/// <param name="CreatedAtUtc">When the payment was made at Razorpay.</param>
/// <param name="PeriodStartUtc">The billing cycle it paid for, when known from Razorpay's invoice. Otherwise the mandate's current cycle is used.</param>
public record RazorpayWebhookPayment(
    string Id,
    long Amount,
    string Currency,
    string Status,
    string? Method,
    string? InvoiceId,
    string? OrderId,
    string? ErrorDescription,
    DateTime? CreatedAtUtc,
    DateTime? PeriodStartUtc = null,
    DateTime? PeriodEndUtc = null);

/// <summary>
/// A stored subscription webhook, read back from its raw body: which event, when Razorpay raised it, the subscription as it stood at that
/// moment, and the payment if there was one. Every subscription event carries the whole subscription entity, so each one is a full picture
/// of the mandate at the time - which is what lets them be applied in any order.
/// </summary>
/// <param name="CreatedByThisPlatform">The subscription's notes say this platform's checkout created it. Others on the same Razorpay
/// account (made by hand on the dashboard, say) are none of our business.</param>
public record RazorpaySubscriptionWebhook(
    string Event,
    DateTime? CreatedAtUtc,
    RazorpaySubscriptionInfo? Subscription,
    bool CreatedByThisPlatform,
    RazorpayWebhookPayment? Payment)
{
    /// <summary>Null when the body is not JSON at all.</summary>
    public static RazorpaySubscriptionWebhook? Parse(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var body = root.TryGetProperty("payload", out var p) ? p : default;

            JsonElement? Entity(string kind) =>
                body.ValueKind == JsonValueKind.Object && body.TryGetProperty(kind, out var k) && k.ValueKind == JsonValueKind.Object
                && k.TryGetProperty("entity", out var e) && e.ValueKind == JsonValueKind.Object
                    ? e
                    : null;

            RazorpaySubscriptionInfo? subscription = null;
            var ours = false;
            if (Entity("subscription") is { } s && Str(s, "id") is { } subscriptionId)
            {
                subscription = new RazorpaySubscriptionInfo(
                    subscriptionId, Str(s, "plan_id"), Str(s, "status") ?? "unknown", Str(s, "short_url"),
                    (int)(Long(s, "total_count") ?? 0), (int)(Long(s, "paid_count") ?? 0), (int)(Long(s, "remaining_count") ?? 0),
                    Long(s, "current_start"), Long(s, "current_end"), Long(s, "charge_at"), Long(s, "start_at"), Long(s, "end_at"), Long(s, "ended_at"),
                    Str(s, "customer_id"), Str(s, "payment_method"));
                ours = s.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Object && Str(notes, "source") == "autopay";
            }

            RazorpayWebhookPayment? payment = null;
            if (Entity("payment") is { } pay && Str(pay, "id") is { } paymentId)
            {
                payment = new RazorpayWebhookPayment(
                    paymentId, Long(pay, "amount") ?? 0, Str(pay, "currency") ?? "INR", Str(pay, "status") ?? "unknown", Str(pay, "method"),
                    Str(pay, "invoice_id"), Str(pay, "order_id"), Str(pay, "error_description"), RazorpayMandateState.FromUnix(Long(pay, "created_at")));
            }

            return new RazorpaySubscriptionWebhook(
                Str(root, "event") ?? "unknown", RazorpayMandateState.FromUnix(Long(root, "created_at")), subscription, ours, payment);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
