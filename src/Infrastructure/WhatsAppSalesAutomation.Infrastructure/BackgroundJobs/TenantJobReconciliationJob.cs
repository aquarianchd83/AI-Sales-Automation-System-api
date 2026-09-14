using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// The only platform-global job this phase adds: periodically re-derives every tenant's recurring job
/// registrations from the <c>TenantJobSchedules</c> table.
///
/// Every path that changes a tenant's status or schedule already calls
/// <c>ITenantJobProvisioner.SyncTenantAsync</c> inline, so this is not how a suspension takes effect and
/// nothing normal depends on it. It is purely a backstop for drift the application never caused:
/// a recurring job deleted by hand from the /hangfire dashboard (that page has a Delete button, and a
/// PlatformSuperAdmin is exactly who is looking at it), a direct database change or restore, or a future
/// code path that changes TenantStatus without calling the provisioner.
///
/// Daily, deliberately. The intended trigger for a reconcile is an operator noticing something wrong and
/// pressing "Reconcile now" in the Platform Admin Console; this scheduled pass is only for when nobody
/// is looking. Running it more often would not make drift less likely, it would just re-register every
/// tenant's every job that many more times a day to change nothing.
/// </summary>
public class TenantJobReconciliationJob
{
    private readonly ITenantJobProvisioner _provisioner;
    private readonly ILogger<TenantJobReconciliationJob> _logger;

    public TenantJobReconciliationJob(ITenantJobProvisioner provisioner, ILogger<TenantJobReconciliationJob> logger)
    {
        _provisioner = provisioner;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            await _provisioner.ReconcileAllAsync();
        }
        catch (Exception ex)
        {
            // Must never throw: a permanently-failing reconcile job in the dashboard is both alarming and
            // useless, and the next pass (scheduled or operator-triggered) starts from a clean slate.
            _logger.LogError(ex, "Tenant job reconciliation failed");
        }
    }
}
