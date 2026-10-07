namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Where the credit an AI operation spent came from. A trial allocation is never purchased credit.</summary>
public enum AiUsageSource
{
    Trial = 0,
    Paid = 1
}

public enum AiTransactionStatus
{
    /// <summary>Credit is spent and the provider call is under way.</summary>
    Authorized = 0,

    /// <summary>The provider answered. The credit stays spent.</summary>
    Completed = 1,

    /// <summary>The platform or provider failed. The credit was given back.</summary>
    Failed = 2,

    /// <summary>Refused before anything was spent - see the denial reason.</summary>
    Blocked = 3
}

public enum AiDenialReason
{
    None = 0,
    TrialLimitReached = 1,
    TrialExpired = 2,
    InsufficientCredits = 3,
    SubscriptionExpired = 4,
    SubscriptionCancelled = 5,
    AccountInactive = 6
}
