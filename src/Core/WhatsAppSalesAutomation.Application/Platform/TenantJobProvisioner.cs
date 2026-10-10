using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <inheritdoc />
public class TenantJobProvisioner : ITenantJobProvisioner
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantJobScheduler _scheduler;
    private readonly ILogger<TenantJobProvisioner> _logger;

    public TenantJobProvisioner(
        IApplicationDbContext context,
        ITenantJobScheduler scheduler,
        ILogger<TenantJobProvisioner> logger)
    {
        _context = context;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task SyncTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Tenants carries no query filter (see its own doc comment), so this reads correctly with no
        // ambient tenant - which is the only situation this ever runs in.
        var tenant = await _context.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Status, SetupDone = t.OnboardingCompletedAt != null })
            .FirstOrDefaultAsync(cancellationToken);
        var status = tenant is null ? (TenantStatus?)null : tenant.Status;

        if (status is null)
        {
            // Nothing to schedule for a tenant that isn't there. Still unregister, rather than return
            // early: this is exactly the state a hard-deleted tenant leaves behind, and its
            // registrations would otherwise keep firing against a tenant id with no rows.
            foreach (var definition in TenantJobCatalog.All)
                _scheduler.RemoveTenantJob(tenantId, definition.Key);

            return;
        }

        var hasCampaign = await _context.Campaigns.IgnoreQueryFilters().AnyAsync(c => c.TenantId == tenantId, cancellationToken);
        var hasLeadProfile = await _context.LeadDiscoveryProfiles.IgnoreQueryFilters().AnyAsync(p => p.TenantId == tenantId, cancellationToken);

        var schedules = await GetOrCreateSchedulesAsync(tenantId, hasCampaign, hasLeadProfile, tenant!.SetupDone, cancellationToken);
        ApplyRegistrations(status.Value, schedules);
        RemoveRegistrationsNotNeeded(tenantId, hasCampaign, hasLeadProfile);
    }

    public async Task<TenantJobReconcileSummary> ReconcileAllAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await _context.Tenants
            .Select(t => new { t.Id, t.Status, SetupDone = t.OnboardingCompletedAt != null })
            .ToListAsync(cancellationToken);

        var existing = await _context.TenantJobSchedules.ToListAsync(cancellationToken);
        var tenantsWithCampaigns = (await _context.Campaigns.IgnoreQueryFilters()
            .Select(c => c.TenantId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var tenantsWithLeadProfiles = (await _context.LeadDiscoveryProfiles.IgnoreQueryFilters()
            .Select(p => p.TenantId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var created = 0;
        var rowsRemoved = 0;
        var registered = 0;
        var removed = 0;

        foreach (var tenant in tenants)
        {
            var hasCampaign = tenantsWithCampaigns.Contains(tenant.Id);
            var hasLeadProfile = tenantsWithLeadProfiles.Contains(tenant.Id);
            var schedules = existing.Where(s => s.TenantId == tenant.Id).ToList();

            // Jobs a tenant has been carrying with nothing to do (campaign jobs without a campaign, lead discovery without a
            // profile): take them away, row and registration.
            foreach (var idle in schedules.Where(s => !TenantJobCatalog.IsApplicable(s.JobType, hasCampaign, hasLeadProfile)).ToList())
            {
                _context.TenantJobSchedules.Remove(idle);
                schedules.Remove(idle);
                rowsRemoved++;
            }

            foreach (var definition in TenantJobCatalog.All)
            {
                if (!TenantJobCatalog.IsApplicable(definition.Key, hasCampaign, hasLeadProfile))
                    continue;

                if (schedules.Any(s => s.JobType == definition.Key))
                    continue;

                var schedule = NewSchedule(tenant.Id, definition, tenant.SetupDone);
                _context.TenantJobSchedules.Add(schedule);
                schedules.Add(schedule);
                created++;
            }

            var (added, dropped) = ApplyRegistrations(tenant.Status, schedules);
            registered += added;
            removed += dropped;
            removed += RemoveRegistrationsNotNeeded(tenant.Id, hasCampaign, hasLeadProfile);
        }

        if (created > 0 || rowsRemoved > 0)
        {
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // Same race as GetOrCreateSchedulesAsync's, from the other direction - another instance
                // booting at the same moment provisioned the same new tenant first. The registrations
                // below are driven by the in-memory rows either way, so the pass still leaves Hangfire
                // correct; only the (already existing) rows failed to be written.
                _logger.LogWarning(ex, "Tenant job reconcile could not create {Created} schedule row(s) - another writer got there first", created);
                created = 0;
            }
        }

        var orphans = RemoveOrphanRegistrations(tenants.Select(t => t.Id).ToHashSet());

        var summary = new TenantJobReconcileSummary(tenants.Count, created, registered, removed, orphans);

        // Only worth a line when it actually changed something - this runs on a schedule, and a
        // steady-state pass that registers the same jobs at the same crons is pure noise otherwise.
        if (created > 0 || rowsRemoved > 0 || orphans > 0)
            _logger.LogInformation(
                "Tenant job reconcile: tenants={Tenants} schedulesCreated={Created} idleSchedulesRemoved={Idle} registered={Registered} removed={Removed} orphansRemoved={Orphans}",
                summary.TenantsExamined, summary.SchedulesCreated, rowsRemoved, summary.JobsRegistered, summary.JobsRemoved, summary.OrphanRegistrationsRemoved);

        return summary;
    }

    private async Task<List<TenantJobSchedule>> GetOrCreateSchedulesAsync(Guid tenantId, bool hasCampaign, bool hasLeadProfile, bool setupDone, CancellationToken cancellationToken)
    {
        var schedules = await _context.TenantJobSchedules
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // The campaign jobs exist only while the tenant has a campaign, the lead discovery job only once it has a profile.
        var idle = schedules.Where(s => !TenantJobCatalog.IsApplicable(s.JobType, hasCampaign, hasLeadProfile)).ToList();
        if (idle.Count > 0)
        {
            _context.TenantJobSchedules.RemoveRange(idle);
            await _context.SaveChangesAsync(cancellationToken);
            schedules.RemoveAll(idle.Contains);
        }

        var missing = TenantJobCatalog.All
            .Where(definition => TenantJobCatalog.IsApplicable(definition.Key, hasCampaign, hasLeadProfile))
            .Where(definition => schedules.All(s => s.JobType != definition.Key))
            .Select(definition => NewSchedule(tenantId, definition, setupDone))
            .ToList();

        if (missing.Count == 0)
            return schedules;

        _context.TenantJobSchedules.AddRange(missing);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            schedules.AddRange(missing);
            return schedules;
        }
        catch (DbUpdateException)
        {
            // Lost a race to create the same rows - this runs from several places at once for a brand new
            // tenant (its creation, a boot or scheduled reconcile, and any console read), and the unique
            // (TenantId, JobType) index is what stops a duplicate rather than letting two schedules for
            // the same job silently disagree. The other writer's rows are just as good as ours, so drop
            // ours and read theirs instead of failing the caller.
            foreach (var entry in missing)
                _context.TenantJobSchedules.Remove(entry);

            return await _context.TenantJobSchedules
                .Where(s => s.TenantId == tenantId)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }
    }

    /// <summary>Lead discovery is created paused until the tenant has finished setting up (it would otherwise start researching and messaging
    /// businesses for a tenant that has not yet connected WhatsApp or written its templates); every other job starts on.</summary>
    private static TenantJobSchedule NewSchedule(Guid tenantId, TenantJobDefinition definition, bool setupDone) => new()
    {
        TenantId = tenantId,
        JobType = definition.Key,
        CronExpression = definition.DefaultCron,
        IsEnabled = definition.Key != TenantJobTypes.LeadDiscovery || setupDone
    };

    public async Task EnableLeadDiscoveryAfterSetupAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var schedule = await _context.TenantJobSchedules
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.JobType == TenantJobTypes.LeadDiscovery, cancellationToken);

        // No row yet means no profile yet: it will be created already switched on, since the setup is done.
        if (schedule is { IsEnabled: false })
        {
            schedule.IsEnabled = true;
            await _context.SaveChangesAsync(cancellationToken);
        }

        await SyncTenantAsync(tenantId, cancellationToken);
    }

    /// <summary>Registers every enabled job for an eligible tenant and removes the rest. A schedule row
    /// for a job type no longer in the catalog is skipped rather than registered - the row is kept (it
    /// may be a job type a rollback would bring back) but nothing tries to schedule a job that no
    /// longer exists.</summary>
    private (int Registered, int Removed) ApplyRegistrations(TenantStatus status, IReadOnlyCollection<TenantJobSchedule> schedules)
    {
        var eligible = TenantStatusRules.AllowsBackgroundJobs(status);
        int registered = 0, removed = 0;

        foreach (var schedule in schedules)
        {
            if (!TenantJobCatalog.IsKnown(schedule.JobType))
                continue;

            if (eligible && schedule.IsEnabled)
            {
                _scheduler.AddOrUpdateTenantJob(schedule.TenantId, schedule.JobType, schedule.CronExpression);
                registered++;
            }
            else
            {
                _scheduler.RemoveTenantJob(schedule.TenantId, schedule.JobType);
                removed++;
            }
        }

        return (registered, removed);
    }

    /// <summary>Drops per-tenant registrations whose tenant no longer exists at all. A tenant that still
    /// exists but should not run anything is handled by <see cref="ApplyRegistrations"/> above; this is
    /// only for ids that have no row left to drive that decision.</summary>
    /// <summary>Unregisters the jobs the tenant has nothing yet for (campaign jobs without a campaign, lead discovery
    /// without a profile). Idempotent: removing what is not registered does nothing. Returns how many it asked to remove.</summary>
    private int RemoveRegistrationsNotNeeded(Guid tenantId, bool hasCampaign, bool hasLeadProfile)
    {
        var removed = 0;
        foreach (var definition in TenantJobCatalog.All.Where(d => !TenantJobCatalog.IsApplicable(d.Key, hasCampaign, hasLeadProfile)))
        {
            _scheduler.RemoveTenantJob(tenantId, definition.Key);
            removed++;
        }

        return removed;
    }

    private int RemoveOrphanRegistrations(HashSet<Guid> knownTenantIds)
    {
        var removed = 0;

        foreach (var recurringJobId in _scheduler.GetRegisteredTenantJobIds())
        {
            var separator = recurringJobId.LastIndexOf(':');
            if (separator < 0)
                continue;

            var jobType = recurringJobId[..separator];
            if (!Guid.TryParse(recurringJobId[(separator + 1)..], out var tenantId) || knownTenantIds.Contains(tenantId))
                continue;

            _scheduler.RemoveTenantJob(tenantId, jobType);
            removed++;
        }

        return removed;
    }
}
