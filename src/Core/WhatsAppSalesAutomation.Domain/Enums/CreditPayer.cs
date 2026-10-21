namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Whose balance a credit comes out of.</summary>
public enum CreditPayer
{
    /// <summary>The tenant's own prepaid quota.</summary>
    Tenant = 0,

    /// <summary>The platform's (product owner's) credit wallet.</summary>
    Platform = 1
}

/// <summary>Where a credit spend stands. Only <see cref="Charged"/> and <see cref="Unpaid"/> are open to a platform admin to act on.</summary>
public enum CreditUsageStatus
{
    /// <summary>Spent from the payer's balance.</summary>
    Charged = 0,

    /// <summary>Given back automatically because the work failed before it was delivered.</summary>
    Reversed = 1,

    /// <summary>The tenant's charge was given back by a platform admin as a goodwill gesture.</summary>
    Redeemed = 2,

    /// <summary>The work was already done when neither balance could pay for it, so nobody was charged.</summary>
    Unpaid = 3
}

public enum PlatformCreditEntryType
{
    TopUp = 0,
    Consumption = 1,
    Reversal = 2,
    AdjustmentCredit = 3,
    AdjustmentDebit = 4
}
