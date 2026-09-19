namespace WhatsAppSalesAutomation.Domain.Enums;

public enum PlatformNotificationKind
{
    /// <summary>A tenant's background job has failed several runs in a row.</summary>
    JobFailing = 0,

    /// <summary>A job that had raised <see cref="JobFailing"/> ran successfully again.</summary>
    JobRecovered = 1
}

public enum PlatformNotificationSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}
