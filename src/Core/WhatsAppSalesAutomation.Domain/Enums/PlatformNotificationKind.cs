namespace WhatsAppSalesAutomation.Domain.Enums;

public enum PlatformNotificationKind
{
    /// <summary>A tenant's background job has failed several runs in a row.</summary>
    JobFailing = 0,

    /// <summary>A job that had raised <see cref="JobFailing"/> ran successfully again.</summary>
    JobRecovered = 1,

    /// <summary>A new company signed up (self-service) and started a trial.</summary>
    TenantSignedUp = 2,

    /// <summary>A tenant asked for a refund - it waits for an operator to approve or reject it (and lapses if nobody does).</summary>
    RefundRequested = 3,

    /// <summary>An approved refund did not go through at the payment gateway; the money is still held until an operator retries or rejects.</summary>
    RefundFailed = 4,

    /// <summary>A tenant's plan period ended and could not be renewed - the plan has no price for the tenant's country.</summary>
    PlanRenewalFailed = 5
}

public enum PlatformNotificationSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}
