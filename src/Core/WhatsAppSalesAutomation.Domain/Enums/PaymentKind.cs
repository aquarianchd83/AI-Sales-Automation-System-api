namespace WhatsAppSalesAutomation.Domain.Enums;

public enum PaymentKind
{
    Subscription = 0,
    CreditPack = 1,
    Refund = 2,

    /// <summary>A prepaid wallet top-up: the tenant paid an amount of rupees (through Razorpay) for credits, not for a plan or a pack.</summary>
    WalletTopUp = 3
}
