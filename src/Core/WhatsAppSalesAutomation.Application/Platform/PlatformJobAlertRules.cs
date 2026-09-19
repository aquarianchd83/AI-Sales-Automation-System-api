namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>When a run of failures is worth telling the platform operators about. A streak is counted in
/// runs, so the same count means very different waits: three failures of a minutely job is three minutes, of
/// a daily job three days.</summary>
public static class PlatformJobAlertRules
{
    private const int DefaultThreshold = 3;

    /// <summary>Consecutive failed runs before the operators are alerted. Once-a-day jobs alert on the first
    /// failure - waiting for three would mean finding out days late - while frequent jobs need a streak so a
    /// single transient blip stays noise.</summary>
    public static int FailureThreshold(string jobType) => jobType switch
    {
        TenantJobTypes.WhatsAppTokenRefresh => 1,
        TenantJobTypes.LeadDiscovery => 1,
        _ => DefaultThreshold
    };
}
