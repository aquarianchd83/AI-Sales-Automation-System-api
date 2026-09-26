namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Stable keys for the recurring jobs that run once per tenant. Doubles as the prefix of the
/// Hangfire recurring job id (<c>{JobType}:{TenantId}</c>) and as <c>TenantJobSchedule.JobType</c>'s
/// stored value, so these strings are data, not labels - renaming one orphans every existing schedule
/// row and Hangfire registration that used it.
///
/// Deliberately the same ids the four jobs were registered under while they were still global
/// fan-out jobs, so the legacy registrations these replace are recognisable (and removable - see
/// <c>RecurringJobsRegistrar</c>) rather than silently left behind under an unrelated name.</summary>
public static class TenantJobTypes
{
    public const string CampaignInitialSends = "campaign-initial-sends";
    public const string CampaignFollowUps = "campaign-follow-ups";
    public const string CampaignSendRetries = "campaign-send-retries";
    public const string WhatsAppTemplateSync = "whatsapp-template-sync";
    public const string WhatsAppTokenRefresh = "whatsapp-token-refresh";
    public const string LeadDiscovery = "lead-discovery";
}

/// <summary>One per-tenant job as the Platform Admin Console needs to describe it. <see cref="Key"/> is
/// the only part that reaches the database; the rest exists so the console can render a job an operator
/// has never seen before without a parallel table of hard-coded copy in the frontend.</summary>
public record TenantJobDefinition(
    string Key,
    string DisplayName,
    string Description,
    string DefaultCron);

/// <summary>
/// The single list of what "per-tenant background jobs" means, shared by everything that has to agree
/// on it: the provisioner (which rows to create for a new tenant), the scheduler (which CLR job type
/// each key maps to - see <c>HangfireTenantJobScheduler</c>), and the Platform Admin Console's own job
/// list. Adding an entry is all it takes for every tenant to get one: the next reconcile pass (at boot,
/// on demand, or daily) creates each tenant's schedule row for it and registers it.
///
/// <c>whatsapp-token-refresh</c> joined later than the others. It was a single platform-global job until
/// each tenant brought its own WhatsApp token - see <c>WhatsAppTokenRefreshJob</c>'s own doc comment -
/// and keeping the same id as its prefix is what lets RecurringJobsRegistrar remove the old global
/// registration.
/// </summary>
public static class TenantJobCatalog
{
    public static readonly IReadOnlyList<TenantJobDefinition> All = new[]
    {
        new TenantJobDefinition(
            TenantJobTypes.CampaignInitialSends,
            "Campaign initial sends",
            "Sends the first message of each campaign step to customers who have just become due.",
            "* * * * *"),
        new TenantJobDefinition(
            TenantJobTypes.CampaignFollowUps,
            "Campaign follow-ups",
            "Sends scheduled follow-up messages to customers who did not respond to an earlier step.",
            "* * * * *"),
        new TenantJobDefinition(
            TenantJobTypes.CampaignSendRetries,
            "Campaign send retries",
            "Retries sends that failed transiently (WhatsApp API errors, rate limits).",
            "*/5 * * * *"),
        new TenantJobDefinition(
            TenantJobTypes.WhatsAppTemplateSync,
            "WhatsApp template sync",
            "Pushes new/changed message templates to Meta and pulls their review status back.",
            "0 * * * *"),
        new TenantJobDefinition(
            TenantJobTypes.WhatsAppTokenRefresh,
            "WhatsApp token refresh",
            "Exchanges the tenant's Meta access token for a fresh one before it expires. Does nothing for a token Meta reports never expires.",
            "0 0 * * *"),
        new TenantJobDefinition(
            TenantJobTypes.LeadDiscovery,
            "Lead discovery",
            "Searches the web for businesses matching the tenant's lead discovery profile, verifies their contact details and saves the qualified, new ones. Does nothing until the tenant enables a profile.",
            "0 2 * * *")
    };

    /// <summary>Job types a tenant's own Admin may view, reschedule and trigger via the tenant-facing
    /// Jobs screen (<c>TenantJobsController</c>) - campaign sending and lead discovery, the jobs whose
    /// output the tenant directly owns the consequences of. WhatsApp template sync and token refresh
    /// stay operator-only via <c>PlatformJobsController</c>: they are integration plumbing where a wrong
    /// schedule can silently break the tenant's own WhatsApp connection, not something to hand a tenant
    /// Admin a footgun for.</summary>
    public static readonly IReadOnlySet<string> SelfServiceKeys = new HashSet<string>
    {
        TenantJobTypes.CampaignInitialSends,
        TenantJobTypes.CampaignFollowUps,
        TenantJobTypes.CampaignSendRetries,
        TenantJobTypes.LeadDiscovery,
    };

    public static TenantJobDefinition? Find(string jobType) =>
        All.FirstOrDefault(j => j.Key == jobType);

    public static bool IsKnown(string jobType) => Find(jobType) is not null;

    /// <summary>The Hangfire recurring job id for one tenant's copy of one job. Tenant id last so the
    /// dashboard's alphabetical listing groups every tenant's copies of the same job together, which is
    /// how an operator reads that screen ("is template sync healthy?"), rather than interleaving all
    /// four jobs per tenant.</summary>
    public static string RecurringJobId(string jobType, Guid tenantId) => $"{jobType}:{tenantId}";
}
