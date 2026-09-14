using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire's <c>[DisableConcurrentExecution]</c>, scoped to one tenant instead of one method.
///
/// The built-in attribute builds its lock resource from the job's type and method name only, ignoring
/// arguments. That was the right behaviour while these jobs were global fan-outs (one execution, all
/// tenants), but with one recurring job per tenant it would put every tenant behind a single
/// platform-wide lock: every tenant's minutely run would queue on the one before it and, past a handful
/// of tenants, start failing on the lock timeout instead of running. Including the tenant id in the
/// resource restores the original intent - a tenant's run cannot overlap its own previous run, and has
/// nothing to do with any other tenant's.
///
/// This remains the third line of defence against a double send, behind the idempotency key and its
/// unique index - see CampaignSendService's own remarks.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class DisableConcurrentExecutionPerTenantAttribute : JobFilterAttribute, IServerFilter
{
    private const string LockKey = "DisableConcurrentExecutionPerTenant:Lock";

    private readonly int _timeoutInSeconds;

    public DisableConcurrentExecutionPerTenantAttribute(int timeoutInSeconds)
    {
        if (timeoutInSeconds < 0)
            throw new ArgumentException("Timeout argument value should be greater than zero.", nameof(timeoutInSeconds));

        _timeoutInSeconds = timeoutInSeconds;
    }

    public void OnPerforming(PerformingContext filterContext)
    {
        var resource = GetResource(filterContext.BackgroundJob.Job);
        var distributedLock = filterContext.Connection.AcquireDistributedLock(resource, TimeSpan.FromSeconds(_timeoutInSeconds));
        filterContext.Items[LockKey] = distributedLock;
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        if (!filterContext.Items.TryGetValue(LockKey, out var acquired) || acquired is not IDisposable distributedLock)
            return;

        distributedLock.Dispose();
        filterContext.Items.Remove(LockKey);
    }

    /// <summary>Method identity plus the tenant the run is for. Falls back to method identity alone when
    /// no <see cref="Guid"/> argument is present, which is the built-in attribute's exact behaviour - so
    /// putting this on a job that is not per-tenant is conservative rather than wrong.</summary>
    private static string GetResource(Job job)
    {
        var resource = $"{job.Type.FullName}.{job.Method.Name}";
        var tenantId = job.Args?.OfType<Guid>().FirstOrDefault() ?? Guid.Empty;

        return tenantId == Guid.Empty ? resource : $"{resource}:{tenantId}";
    }
}
