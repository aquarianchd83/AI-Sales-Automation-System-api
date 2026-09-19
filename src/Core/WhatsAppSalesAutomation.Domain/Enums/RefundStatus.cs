namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Where a refund request stands. Never automatic: a request waits in Requested until a
/// PlatformSuperAdmin approves or rejects it, and a request nobody answers is closed as Expired.</summary>
public enum RefundStatus
{
    Requested = 0,
    Refunded = 1,
    Rejected = 2,

    /// <summary>Approved but the payment gateway refused or errored - the units stay held and an operator
    /// retries or rejects it.</summary>
    Failed = 3,

    /// <summary>Withdrawn by the tenant while still Requested.</summary>
    Cancelled = 4,

    Expired = 5
}
