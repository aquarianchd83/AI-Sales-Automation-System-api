using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// The only platform-global job this phase adds: periodically re-derives every tenant's recurring job
/// registrations from the <c>TenantJobSchedules</c> table.
///
/// Every path that changes a tenant's status already calls <c>ITenantJobProvisioner.SyncTenantAsync</c>
/// inline, so this is not how a suspension takes effect - it is the safety net for the cases an inline
/// call cannot cover: a tenant created or deleted while the process was down, a future code path that
/// changes status without calling the provisioner, and registrations left behind by either. Hourly is
/// deliberately unhurried for that role; the console's own "Reconcile now" is there for when an operator
/// does not want to wait.
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
            // useless, and the next hourly pass will try again from a clean slate anyway.
            _logger.LogError(ex, "Tenant job reconciliation failed");
        }
    }
}
