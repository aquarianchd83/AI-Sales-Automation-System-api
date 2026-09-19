using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>Builds the payment row for a quote, so a subscription, a renewal and a credit purchase all record the same
/// snapshot: the country and state, the price before tax, each tax line, the total paid, and its rupee equivalent.</summary>
public static class PaymentFactory
{
    public static Payment For(
        Guid tenantId, PaymentKind kind, string name, PriceQuote quote, DateTime now,
        Guid? planId = null, Guid? creditPackId = null, DateTime? periodStartUtc = null, DateTime? periodEndUtc = null) => new()
    {
        TenantId = tenantId,
        Kind = kind,
        PlanId = planId,
        CreditPackId = creditPackId,
        PlanName = name,
        AmountCents = quote.SubtotalUsdCents,
        CurrencyCode = quote.CurrencyCode,
        CurrencySymbol = quote.CurrencySymbol,
        LocalAmount = quote.Subtotal,
        CountryCode = quote.CountryCode,
        StateCode = quote.StateCode,
        TaxLocal = quote.Tax,
        TaxLinesJson = quote.TaxLinesJson,
        TotalLocal = quote.Total,
        AmountInr = quote.TotalInr,
        FxRateToInr = quote.FxRateToInr,
        PeriodStartUtc = periodStartUtc,
        PeriodEndUtc = periodEndUtc,
        Provider = "Simulated",
        PaidAtUtc = now
    };
}
