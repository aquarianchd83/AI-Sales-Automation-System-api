using Hangfire;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.LeadDiscovery;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Every minute: starts the lead-discovery run for each tenant that has a scheduled date and time due. The slots are the tenants' own schedule (up to five
/// each, once or daily); this replaces the single cron the lead-discovery job used to have. The slot is moved on before the run is queued, so a time can
/// never start two runs, and the run itself is the ordinary scheduled one - a person's Run now is separate and never touches a slot.
/// </summary>
public class LeadDiscoverySlotJob
{
    private readonly ILeadDiscoveryScheduleService _schedule;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<LeadDiscoverySlotJob> _logger;

    public LeadDiscoverySlotJob(ILeadDiscoveryScheduleService schedule, IBackgroundJobClient backgroundJobs, ILogger<LeadDiscoverySlotJob> logger)
    {
        _schedule = schedule;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            foreach (var tenantId in await _schedule.ClaimDueAsync())
            {
                _backgroundJobs.Enqueue<LeadDiscoveryJob>(job => job.RunAsync(tenantId));
                _logger.LogInformation("Lead discovery scheduled run queued for tenant {TenantId}", tenantId);
            }
        }
        catch (Exception ex)
        {
            // Never throws: the next minute asks again, and a due slot that was not claimed is still due.
            _logger.LogError(ex, "Lead discovery schedule pass failed");
        }
    }
}
