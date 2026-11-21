using System.Text.Json.Serialization;
using WhatsAppSalesAutomation.Application.Billing;

namespace WhatsAppSalesAutomation.Application.Wallet;

/// <summary>
/// Bound from "Wallet" (AppSettings table, edited on the Platform Admin Console's Configuration page - no deploy needed). Razorpay's own keys stay
/// under "Razorpay", where the Test/Live switch is simply which key pair is saved.
/// </summary>
public class WalletRechargeOptions
{
    public const string SectionName = "Wallet";

    /// <summary>Smallest top-up in rupees, before tax.</summary>
    public decimal MinRechargeInr { get; set; } = 100m;

    /// <summary>Largest top-up in rupees, before tax.</summary>
    public decimal MaxRechargeInr { get; set; } = 50_000m;

    /// <summary>Wallet credits one rupee buys. The wallet holds credits, not rupees, so this is the explicit conversion between them.</summary>
    public decimal CreditsPerRupee { get; set; } = 1m;

    /// <summary>The quick-pick amounts, comma separated.</summary>
    public string RechargePresets { get; set; } = "100,500,1000,2000,5000";

    /// <summary>The name Razorpay Checkout shows as the merchant.</summary>
    public string BusinessName { get; set; } = "Sales Automation";

    /// <summary>How long an order may sit with no payment before the reconciliation job calls the checkout abandoned.</summary>
    public int AbandonAfterMinutes { get; set; } = 60;

    public IReadOnlyList<decimal> PresetList() =>
        RechargePresets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => decimal.TryParse(p, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1m)
            .Where(v => v >= MinRechargeInr && v <= MaxRechargeInr)
            .Distinct().OrderBy(v => v).ToList();
}

/// <param name="Balance">The tenant's wallet balance in credits, before any payment.</param>
public record RechargeOptionsDto(string Currency, decimal MinAmount, decimal MaxAmount, decimal CreditsPerRupee, IReadOnlyList<decimal> Presets, decimal Balance);

/// <summary>What a top-up would cost: the amount that becomes credits, the tax on top, and the total payable.</summary>
public record RechargeQuoteDto(
    decimal Amount, decimal Tax, decimal Total, string Currency, IReadOnlyList<TaxLineDto> TaxLines, decimal Credits, decimal BalanceBefore, decimal BalanceAfter);

public record WalletBalanceDto(decimal Balance, string Unit);

/// <param name="Amount">Rupees, before tax. The server re-prices it; the browser's figure is never trusted for anything but this request.</param>
public record CreateRechargeOrderRequest(decimal Amount, string? Currency);

/// <summary>Everything Razorpay Checkout needs. The key id is public by design; the key secret never leaves the server.</summary>
public record RechargeCheckoutDto(
    Guid TransactionId,
    string Reference,
    string OrderId,
    string KeyId,
    string Mode,
    long AmountMinor,
    string Currency,
    string BusinessName,
    string Description,
    decimal Amount,
    decimal Tax,
    decimal Total,
    decimal Credits,
    RechargePrefillDto Prefill);

public record RechargePrefillDto(string? Name, string? Email, string? Contact);

/// <summary>What Checkout's success handler returns, passed on untouched (Razorpay's own field names).</summary>
public record VerifyRechargePaymentRequest(
    [property: JsonPropertyName("razorpay_order_id")] string RazorpayOrderId,
    [property: JsonPropertyName("razorpay_payment_id")] string RazorpayPaymentId,
    [property: JsonPropertyName("razorpay_signature")] string RazorpaySignature);

/// <param name="Success">True only when the money is captured and the wallet credited (now or already).</param>
/// <param name="Pending">True when the payment is genuine but not confirmed yet - the webhook or reconciliation settles it.</param>
public record RechargeVerifyResultDto(bool Success, bool Pending, string Message, RechargeTransactionDto? Transaction, decimal? Balance);

public record RechargeTransactionDto(
    Guid Id,
    string Reference,
    string RazorpayOrderId,
    string? RazorpayPaymentId,
    decimal Amount,
    decimal Tax,
    decimal Total,
    string Currency,
    decimal Credits,
    string Status,
    string? PaymentMethod,
    string? FailureReason,
    string Mode,
    decimal? BalanceBefore,
    decimal? BalanceAfter,
    DateTime CreatedAt,
    DateTime? CapturedAt,
    DateTime? CreditedAt);

/// <summary>The parts of a Razorpay webhook a wallet top-up needs, already read out of the signed body.</summary>
public record WalletWebhookEvent(
    string Event,
    string? OrderId,
    string? PaymentId,
    string? Method,
    string? ErrorDescription,
    long? PaymentAmount,
    string? PaymentCurrency,
    string? PaymentStatus,
    string? RefundId,
    long? RefundAmount,
    long? PaymentAmountRefunded);

/// <param name="Matched">The event belongs to a wallet top-up (whether or not it changed anything).</param>
/// <param name="Applied">It changed the transaction - credited it, failed it, or recorded a refund.</param>
public record WalletWebhookResult(bool Matched, bool Applied, string Note)
{
    public static readonly WalletWebhookResult NotOurs = new(false, false, string.Empty);
}
