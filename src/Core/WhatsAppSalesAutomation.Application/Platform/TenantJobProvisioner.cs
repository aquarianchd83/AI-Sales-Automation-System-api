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
        var status = await _context.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => (TenantStatus?)t.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is null)
        {
            // Nothing to schedule for a tenant that isn't there. Still unregister, rather than return
            // early: this is exactly the state a hard-deleted tenant leaves behind, and its
            // registrations would otherwise keep firing against a tenant id with no rows.
            foreach (var definition in TenantJobCatalog.All)
                _scheduler.RemoveTenantJob(tenantId, definition.Key);

            return;
        }

        var schedules = await GetOrCreateSchedulesAsync(tenantId, cancellationToken);
        ApplyRegistrations(status.Value, schedules);
    }

    public async Task<TenantJobReconcileSummary> ReconcileAllAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await _context.Tenants
            .Select(t => new { t.Id, t.Status })
            .ToListAsync(cancellationToken);

        var existing = await _context.TenantJobSchedules.ToListAsync(cancellationToken);
        var created = 0;
        var registered = 0;
        var removed = 0;

        foreach (var tenant in tenants)
        {
            var schedules = existing.Where(s => s.TenantId == tenant.Id).ToList();

            foreach (var definition in TenantJobCatalog.All)
            {
                if (schedules.Any(s => s.JobType == definition.Key))
                    continue;

                var schedule = NewSchedule(tenant.Id, definition);
                _context.TenantJobSchedules.Add(schedule);
                schedules.Add(schedule);
                created++;
            }

            var (added, dropped) = ApplyRegistrations(tenant.Status, schedules);
            registered += added;
            removed += dropped;
        }

        if (created > 0)
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
        if (created > 0 || orphans > 0)
            _logger.LogInformation(
                "Tenant job reconcile: tenants={Tenants} schedulesCreated={Created} registered={Registered} removed={Removed} orphansRemoved={Orphans}",
                summary.TenantsExamined, summary.SchedulesCreated, summary.JobsRegistered, summary.JobsRemoved, summary.OrphanRegistrationsRemoved);

        return summary;
    }

    private async Task<List<TenantJobSchedule>> GetOrCreateSchedulesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var schedules = await _context.TenantJobSchedules
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var missing = TenantJobCatalog.All
            .Where(definition => schedules.All(s => s.JobType != definition.Key))
            .Select(definition => NewSchedule(tenantId, definition))
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

    private static TenantJobSchedule NewSchedule(Guid tenantId, TenantJobDefinition definition) => new()
    {
        TenantId = tenantId,
        JobType = definition.Key,
        CronExpression = definition.DefaultCron,
        IsEnabled = true
    };

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
