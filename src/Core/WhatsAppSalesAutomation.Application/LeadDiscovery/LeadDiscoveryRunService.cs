using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Application.LeadDiscovery.Execution;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>
/// One tenant's lead discovery processing, as a series of recorded executions (LeadDiscoveryExecution).
///
/// <b>Execution.</b> Each execution gets a new Execution ID and a new lock token, and must hold the
/// tenant+profile distributed lock (<see cref="LeadDiscoveryLease"/>) before doing anything. A blocked
/// execution records itself and exits. Every unit of business work starts behind a lock checkpoint, so an
/// execution whose lock expired or was lost stops starting new work - what it already committed stays.
///
/// <b>Discovery.</b> Ask the agent for candidates, qualify them (LeadQualification), drop the known ones,
/// then process each survivor in its own transaction: duplicate re-check, DiscoveredLead + Customer insert
/// (WhatsApp consent OptedIn), and its history row flipped to Created - all committed or rolled back together.
/// One customer's failure never rolls back another's. Repeated for up to MaxRounds rounds while the batch is
/// not full.
///
/// <b>Auto-Campaign.</b> When the profile has Auto-Campaign configured and the execution chain created at least
/// one customer: create (or reuse) the one campaign for (tenant, profile, auto campaign, processing date),
/// associate the referred campaign's templates (its steps) that are still missing, map the newly created
/// customers that are still unmapped, then start the campaign. Each is its own transaction; a failure rolls
/// back only itself and leaves its step RetryPending.
///
/// <b>Retry.</b> A retry is a new execution that resumes its predecessor rather than re-running the job: it
/// carries over the customers the predecessor left Pending or Failed (their candidates were stored), marks the
/// ones already created as skipped, and re-runs the campaign steps, each of which only does what is missing.
/// Scheduled runs resume RetryPending executions automatically, up to MaxRetryAttempts; any retryable
/// execution can also be retried by hand from Lead Discovery History.
/// </summary>
public class LeadDiscoveryRunService : ILeadDiscoveryRunService
{
    /// <summary>Customer.Source (and OptInSource) for a business the discovery job found.</summary>
    private const string CustomerSource = "Lead discovery";

    /// <summary>Customer.FirstName's column length.</summary>
    private const int CustomerNameLength = 100;

    /// <summary>Campaign.Name's column length.</summary>
    private const int CampaignNameMaxLength = 200;

    /// <summary>How many RetryPending executions one scheduled run resumes before discovering afresh.</summary>
    private const int MaxAutomaticRetriesPerRun = 5;

    private const int ErrorMessageLength = 2000;
    private const int ErrorDetailsLength = 8000;

    private readonly IApplicationDbContext _context;
    private readonly ILeadDiscoveryAgent _agent;
    private readonly IPlanLimitsService _planLimits;
    private readonly IQuotaGate _quota;
    private readonly IQuotaAlertService _quotaAlerts;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantTimeZoneProvider _tenantTimeZone;
    private readonly ICampaignService _campaignService;
    private readonly ILeadDiscoveryLockStore _lockStore;
    private readonly IApplicationInstance _instance;
    private readonly IPhoneVerificationProvider _verification;
    private readonly IConsentInvitationService _consent;
    private readonly IConsentPageGate? _consentPageGate;
    private readonly ILogger<LeadDiscoveryRunService> _logger;
    private readonly LeadDiscoveryOptions _options;
    private readonly LeadDiscoveryPricingOptions _pricing;

    public LeadDiscoveryRunService(
        IApplicationDbContext context,
        ILeadDiscoveryAgent agent,
        IPlanLimitsService planLimits,
        IQuotaGate quota,
        IQuotaAlertService quotaAlerts,
        IDateTimeProvider dateTime,
        ITenantTimeZoneProvider tenantTimeZone,
        ICampaignService campaignService,
        ILeadDiscoveryLockStore lockStore,
        IApplicationInstance instance,
        ILogger<LeadDiscoveryRunService> logger,
        IOptionsSnapshot<LeadDiscoveryOptions> options,
        IOptionsSnapshot<LeadDiscoveryPricingOptions> pricing,
        IPhoneVerificationProvider verification,
        IConsentInvitationService consent,
        IConsentPageGate? consentPageGate = null)
    {
        _verification = verification;
        _consent = consent;
        _consentPageGate = consentPageGate;
        _context = context;
        _agent = agent;
        _planLimits = planLimits;
        _quota = quota;
        _quotaAlerts = quotaAlerts;
        _dateTime = dateTime;
        _tenantTimeZone = tenantTimeZone;
        _campaignService = campaignService;
        _lockStore = lockStore;
        _instance = instance;
        _logger = logger;
        _options = options.Value;
        _pricing = pricing.Value;
    }

    public Task<string> RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        RunCoreAsync(tenantId, startedBy: null, cancellationToken);

    public Task<string> RunManuallyAsync(Guid tenantId, string? startedBy, CancellationToken cancellationToken = default) =>
        RunCoreAsync(tenantId, string.IsNullOrWhiteSpace(startedBy) ? "a team member" : startedBy.Trim(), cancellationToken);

    private async Task<string> RunCoreAsync(Guid tenantId, string? startedBy, CancellationToken cancellationToken)
    {
        var profile = await LoadProfileAsync(tenantId, null, cancellationToken);

        if (profile is null)
            return "Skipped: no lead discovery profile has been set up.";
        if (!profile.IsEnabled)
        {
            // A person who pressed Run now must see why nothing started: a history row says it. The schedule's own firing stays quiet, as before.
            const string off = "Skipped: lead discovery is turned off in the profile.";
            return startedBy is null ? off : await RecordSkipAsync(profile, off, quotaExhausted: false, cancellationToken, startedBy: startedBy);
        }

        var summaries = new List<string>();

        // Invitations the SMS provider turned away earlier are tried again first, while their attempt budget lasts. A problem here
        // never stops the discovery itself.
        if (profile.ConsentSmsEnabled)
        {
            try
            {
                var retriedInvitations = await _consent.RetryDueAsync(cancellationToken);
                if (retriedInvitations > 0)
                    summaries.Add($"consent SMS retried: {retriedInvitations}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Retrying consent invitations failed for tenant {TenantId}", tenantId);
            }
        }

        // Before anything that searches, spends or resumes spending: the tenant's WhatsApp Consent Page must be ready.
        if (await ConsentPageBlockAsync(profile, cancellationToken) is { } blocked)
        {
            summaries.Add(await RecordSkipAsync(profile, blocked, quotaExhausted: false, cancellationToken));
            return string.Join(" | ", summaries);
        }

        // Unfinished work first, oldest first - each resumed by its own new execution.
        var retried = false;
        // A person's Run now is its own run: it never takes over a waiting retry, which stays for the next scheduled time.
        foreach (var retryId in startedBy is null ? await FindAutomaticRetryTargetsAsync(profile, cancellationToken) : new List<Guid>())
        {
            var previous = await _context.LeadDiscoveryExecutions
                .FirstOrDefaultAsync(e => e.Id == retryId && e.TenantId == tenantId, cancellationToken);
            if (previous is not null && LeadDiscoveryRetryRules.CanRetry(previous))
            {
                // Started by the schedule, or by a person's Run now: the retry is that firing's run, so it carries the same label.
                summaries.Add("retry: " + await ExecuteAsync(profile, previous, LeadDiscoveryTriggers.AutomaticRetry, 0, cancellationToken, startedBy));
                retried = true;
            }
        }

        // One run per firing: a run that was waiting to finish takes this firing's place, so the tenant never sees a second, new run a second later.
        // The next firing starts new work once nothing is waiting.
        if (!retried)
            summaries.Add(await RunFreshAsync(profile, startedBy, cancellationToken));
        return string.Join(" | ", summaries);
    }

    public async Task<string> RetryExecutionAsync(Guid tenantId, Guid executionId, CancellationToken cancellationToken = default)
    {
        var previous = await _context.LeadDiscoveryExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId && e.TenantId == tenantId, cancellationToken);

        if (previous is null)
            return $"Skipped: execution {executionId} was not found.";
        if (!LeadDiscoveryRetryRules.CanRetry(previous))
            return $"Skipped: execution {executionId} is {previous.Status} and cannot be retried.";

        var profile = await LoadProfileAsync(tenantId, previous.LeadDiscoveryProfileId, cancellationToken);
        if (profile is null)
            return "Skipped: the lead discovery profile no longer exists.";

        // A retry resumes paid work, so it waits for the consent page too. The execution stays retryable; nothing is recorded or charged.
        if (await ConsentPageBlockAsync(profile, cancellationToken) is { } blocked)
            return blocked;

        return await ExecuteAsync(profile, previous, LeadDiscoveryTriggers.ManualRetry, 0, cancellationToken);
    }

    /// <summary>
    /// Why discovery must not start, or null when it may. Discovery that ends in a consent SMS needs a published, active, valid consent page; without
    /// one the tenant cannot lawfully contact what it finds, so nothing is searched, reserved or charged, and the tenant is told in the app. Checked on
    /// the server on every path that can spend: a scheduled run, an operator's Run now, and a retry.
    /// </summary>
    private async Task<string?> ConsentPageBlockAsync(LeadDiscoveryProfile profile, CancellationToken cancellationToken)
    {
        if (_consentPageGate is null || !profile.ConsentSmsEnabled)
            return null;

        var readiness = await _consentPageGate.EvaluateAsync(profile.TenantId, cancellationToken);
        if (readiness.IsReady)
            return null;

        await _consentPageGate.NotifyAsync(profile.TenantId, readiness, cancellationToken);
        _logger.LogInformation("Lead discovery for tenant {TenantId} not started: {Reason}", profile.TenantId, readiness.Reason);

        return readiness.PlatformProblem
            ? $"Skipped: {readiness.Reason} Nothing was searched and no discovery credits were deducted."
            : $"Skipped: {readiness.Reason} Complete and publish it in WhatsApp Consent Page Settings before starting customer discovery. No discovery credits were deducted.";
    }

    private Task<LeadDiscoveryProfile?> LoadProfileAsync(Guid tenantId, Guid? profileId, CancellationToken cancellationToken) =>
        _context.LeadDiscoveryProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && (profileId == null || p.Id == profileId))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string> RunFreshAsync(LeadDiscoveryProfile profile, string? startedBy, CancellationToken cancellationToken)
    {
        // From here on every firing is recorded in Lead Discovery History, even one that does nothing: the
        // profile is enabled, so the tenant expects a run and needs to see why there was none. (No profile, or
        // a disabled one, is the tenant not using discovery - recording those would fill history with noise.)
        if (profile.Keywords.Count == 0 || profile.Locations.Count == 0)
            return await RecordSkipAsync(profile, "Skipped: the profile needs at least one keyword and one location.",
                quotaExhausted: false, cancellationToken, startedBy: startedBy);

        var planLimit = await _planLimits.GetLeadDiscoveryBatchLimitAsync(profile.TenantId, cancellationToken);
        var batchSize = Math.Min(profile.BatchSize, planLimit ?? int.MaxValue);
        if (batchSize <= 0)
            return await RecordSkipAsync(profile, "Skipped: the tenant's plan allows no discovered leads per run.",
                quotaExhausted: false, cancellationToken, startedBy: startedBy);

        // The plan's other limits are checked before anything is searched or paid for, never after: a run whose customers or campaign would pass
        // the plan's limit is not started, and a batch is cut to the room left for customers.
        if (await CustomerQuota.GetAsync(_context, profile.TenantId, cancellationToken) is { } customerQuota)
        {
            if (customerQuota.Exhausted)
                return await RecordSkipAsync(profile,
                    "Skipped: " + CustomerQuota.LimitReachedMessage(customerQuota) + " Nothing was searched and no discovery credits were deducted.",
                    quotaExhausted: false, cancellationToken, stopReason: CustomerQuota.LimitReachedMessage(customerQuota), startedBy: startedBy);

            batchSize = Math.Min(batchSize, customerQuota.Remaining);
        }

        if (profile.AutoCampaignEnabled && profile.SourceCampaignId is not null)
        {
            try
            {
                await _planLimits.EnsureCanCreateCampaignAsync(profile.TenantId, cancellationToken);
            }
            catch (PlanLimitExceededException limit)
            {
                return await RecordSkipAsync(profile,
                    "Skipped: " + limit.Message + " Nothing was searched and no discovery credits were deducted.",
                    quotaExhausted: false, cancellationToken, stopReason: limit.Message, startedBy: startedBy);
            }
        }

        // Prepaid, but only for results: a tenant is charged for each customer a run creates - never for rejected or duplicate
        // candidates, whatever the research cost. A tenant who cannot afford even one lead gets no run and no agent call, but it is
        // still recorded, the same way a run that ran out of quota partway through is.
        if (await _quota.GetAvailableAsync(profile.TenantId, QuotaType.LeadCandidates, cancellationToken) < 1)
        {
            // Zero affordable has three different causes, and only one of them is "out of credits": a tenant whose wallet is full but whose
            // monthly limit for this feature is used up must be told that, not to buy credits.
            var shortfall = await _quota.GetShortfallAsync(profile.TenantId, QuotaType.LeadCandidates, cancellationToken);
            var outOfCredits = shortfall is null || shortfall.Kind == QuotaShortfallKind.InsufficientCredits;
            if (outOfCredits)
                await NotifyQuotaExhaustedAsync(profile.TenantId, cancellationToken);

            return await RecordSkipAsync(profile,
                "Skipped: " + (shortfall?.Message ?? "no lead-candidate quota left - buy credits or wait for the plan to renew."),
                quotaExhausted: outOfCredits, cancellationToken, stopReason: shortfall?.Message, startedBy: startedBy);
        }

        return await ExecuteAsync(profile, null, startedBy is null ? LeadDiscoveryTriggers.Scheduled : LeadDiscoveryTriggers.ManualRun, batchSize, cancellationToken, startedBy);
    }

    /// <summary>A scheduled firing that cannot start at all, so there is nothing to lock and nothing to process -
    /// this skips ExecuteAsync's lock/lease machinery entirely and records the outcome directly. Completed, like
    /// a mid-run quota exhaustion (DiscoverCustomersAsync, via FinalizeAsync), rather than Failed: there is
    /// nothing a retry could resume, and a Failed row would offer one.</summary>
    private async Task<string> RecordSkipAsync(
        LeadDiscoveryProfile profile, string summary, bool quotaExhausted, CancellationToken cancellationToken, string? stopReason = null, string? startedBy = null)
    {
        var now = _dateTime.UtcNow;
        var trigger = startedBy is null ? LeadDiscoveryTriggers.Scheduled : LeadDiscoveryTriggers.ManualRun;

        // The same "not run" for the same reason as the row before it is that row again, not another row: it says "Skipped 3 times, last ..." so a limit
        // that stays full does not fill the history with identical lines.
        var latest = await _context.LeadDiscoveryExecutions
            .Where(e => e.TenantId == profile.TenantId && e.LeadDiscoveryProfileId == profile.Id)
            .OrderByDescending(e => e.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        // Only the schedule's own repeats join a row. A person's Run now always gets a row of its own, so pressing it is always seen.
        if (startedBy is null && latest is { SkipCount: > 0, StartedBy: null } && latest.Summary == summary)
        {
            latest.SkipCount++;
            latest.LastSkippedAtUtc = now;
            await _context.SaveChangesAsync(cancellationToken);
            return summary;
        }

        var execution = new LeadDiscoveryExecution
        {
            TenantId = profile.TenantId,
            LeadDiscoveryProfileId = profile.Id,
            ProfileName = Truncate(profile.TargetBusinessType, 200) ?? string.Empty,
            ProcessingDate = (await _tenantTimeZone.GetLocalNowAsync(cancellationToken)).Date,
            Trigger = trigger,
            StartedBy = Truncate(startedBy, 200),
            SkipCount = 1,
            StartedAtUtc = now,
            EndedAtUtc = now,
            Status = LeadDiscoveryExecutionStatus.Completed,
            QuotaExhausted = quotaExhausted,
            QuotaStopReason = Truncate(stopReason, 500),
            LockKey = LeadDiscoveryLockClaim.KeyFor(profile.TenantId, profile.Id),
            CampaignStatus = LeadDiscoveryCampaignStatus.Skipped,
            TemplateStatus = LeadDiscoveryAssociationStatus.Skipped,
            MappingStatus = LeadDiscoveryAssociationStatus.Skipped,
            CampaignNote = "Nothing was discovered - this run did not start.",
            // Shown on the history row itself, not only in the detail's Summary.
            ErrorMessage = summary,
            NextRetryInfo = "Nothing to retry. The next scheduled run will try again.",
            Summary = summary
        };
        execution.RootExecutionId = execution.Id;

        _context.LeadDiscoveryExecutions.Add(execution);
        await _context.SaveChangesAsync(cancellationToken);

        return summary;
    }

    /// <summary>Tells the tenant right away rather than waiting for the quota-alerts sweep (up to 15
    /// minutes later) - a discovery run just stopped because of this, so the tenant should hear about it
    /// now. Never lets a notification problem interrupt recording the run itself.</summary>
    private async Task NotifyQuotaExhaustedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await _quotaAlerts.NotifyIfExhaustedAsync(tenantId, QuotaType.LeadCandidates, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not notify tenant {TenantId} of exhausted lead-candidate quota", tenantId);
        }
    }

    private Task<List<Guid>> FindAutomaticRetryTargetsAsync(LeadDiscoveryProfile profile, CancellationToken cancellationToken) =>
        _context.LeadDiscoveryExecutions.AsNoTracking()
            .Where(e => e.TenantId == profile.TenantId
                        && e.LeadDiscoveryProfileId == profile.Id
                        && e.Status == LeadDiscoveryExecutionStatus.RetryPending
                        && e.SupersededByExecutionId == null
                        && e.RetryCount < _options.MaxRetryAttempts)
            .OrderBy(e => e.StartedAtUtc)
            .Take(MaxAutomaticRetriesPerRun)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

    // ------------------------------------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------------------------------------

    /// <param name="previous">The execution being resumed, or null for a fresh discovery.</param>
    /// <param name="batchSize">Fresh discovery only: the most new leads this execution may add.</param>
    private async Task<string> ExecuteAsync(
        LeadDiscoveryProfile profile, LeadDiscoveryExecution? previous, string trigger, int batchSize, CancellationToken cancellationToken, string? startedBy = null)
    {
        var now = _dateTime.UtcNow;
        var processingDate = previous?.ProcessingDate ?? (await _tenantTimeZone.GetLocalNowAsync(cancellationToken)).Date;

        var execution = new LeadDiscoveryExecution
        {
            TenantId = profile.TenantId,
            LeadDiscoveryProfileId = profile.Id,
            ProfileName = Truncate(profile.TargetBusinessType, 200) ?? string.Empty,
            ProcessingDate = processingDate,
            Trigger = trigger,
            StartedBy = Truncate(startedBy, 200),
            StartedAtUtc = now,
            Status = LeadDiscoveryExecutionStatus.Started,
            RetryOfExecutionId = previous?.Id,
            RetryCount = previous is null ? 0 : previous.RetryCount + 1,
            LockKey = LeadDiscoveryLockClaim.KeyFor(profile.TenantId, profile.Id)
        };
        execution.RootExecutionId = previous?.RootExecutionId ?? execution.Id;
        _context.LeadDiscoveryExecutions.Add(execution);
        await _context.SaveChangesAsync(cancellationToken);

        var claim = new LeadDiscoveryLockClaim(profile.TenantId, profile.Id, execution.Id, Guid.NewGuid(), _instance.Id);
        await using var lease = new LeadDiscoveryLease(_lockStore, _dateTime, _logger, LeaseSettings(), claim, processingDate);

        if (!await lease.AcquireAsync(cancellationToken))
            return await FinishBlockedAsync(execution, previous, lease, cancellationToken);

        await RecoverStaleExecutionsAsync(execution, cancellationToken);

        if (previous is not null)
        {
            // Only now, holding the lock: a blocked retry must leave its predecessor retryable.
            previous.SupersededByExecutionId = execution.Id;
            previous.LastRetryAtUtc = now;
        }

        execution.Status = previous is null ? LeadDiscoveryExecutionStatus.Processing : LeadDiscoveryExecutionStatus.Retrying;
        await _context.SaveChangesAsync(cancellationToken);

        lease.StartHeartbeat();
        var run = new ExecutionRun(execution, profile, lease, batchSize);

        try
        {
            if (previous is null)
                await DiscoverCustomersAsync(run, cancellationToken);
            else
                await ResumeCustomersAsync(run, previous, cancellationToken);

            await ProcessAutoCampaignAsync(run, cancellationToken);
        }
        catch (LeadDiscoveryLockUnavailableException ex)
        {
            // Stop starting new work. Everything already committed stays committed; a retry picks up the rest.
            _logger.LogWarning("Lead discovery execution {ExecutionId} stopped at {Step}: {Reason}", execution.Id, run.Step, ex.Message);
            run.Interrupted = true;
            RestoreTracking(execution);
            SetError(execution, run.Step, ex.Message, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Lead discovery execution {ExecutionId} failed at {Step}", execution.Id, run.Step);
            run.Fatal = true;
            RestoreTracking(execution);
            SetError(execution, run.Step, ex.Message, ex.ToString());
        }
        finally
        {
            await lease.ReleaseAsync(CancellationToken.None);
        }

        await FinalizeAsync(run, CancellationToken.None);
        return execution.Summary ?? string.Empty;
    }

    private LeadDiscoveryLeaseSettings LeaseSettings() => new(
        TimeSpan.FromSeconds(Math.Max(1, _options.LockLeaseSeconds)),
        TimeSpan.FromSeconds(Math.Max(0, _options.LockHeartbeatSeconds)),
        TimeSpan.FromSeconds(Math.Max(0, _options.LockRenewWhenRemainingSeconds)));

    /// <summary>A blocked execution never started, so it has nothing to release and changed nothing - it is only
    /// recorded.</summary>
    private async Task<string> FinishBlockedAsync(
        LeadDiscoveryExecution execution, LeadDiscoveryExecution? previous, LeadDiscoveryLease lease, CancellationToken cancellationToken)
    {
        execution.Status = LeadDiscoveryExecutionStatus.Failed;
        execution.FailedStep = LeadDiscoverySteps.LockAcquisition;
        execution.ErrorMessage = lease.BlockedByExecutionId is { } holder
            ? $"Blocked: execution {holder} is already processing this lead discovery profile."
            : "Blocked: the lead discovery lock could not be acquired.";
        execution.NextRetryInfo = previous is null
            ? "Nothing was processed. The next scheduled run will try again."
            : $"Nothing was processed. Execution {previous.Id} is still retryable.";
        execution.EndedAtUtc = _dateTime.UtcNow;
        execution.Summary = $"execution={execution.Id} blocked by={lease.BlockedByExecutionId?.ToString() ?? "unknown"}";
        await _context.SaveChangesAsync(cancellationToken);
        return execution.Summary;
    }

    /// <summary>
    /// Any other execution of this tenant+profile still marked as running is dead: this execution holds the
    /// lock, which it could only take once that execution's lease had elapsed or been released. Its lock is
    /// recorded as Expired and its unfinished work made retryable. Nothing it committed is touched.
    /// </summary>
    private async Task RecoverStaleExecutionsAsync(LeadDiscoveryExecution current, CancellationToken cancellationToken)
    {
        var stale = await _context.LeadDiscoveryExecutions
            .Where(e => e.TenantId == current.TenantId
                        && e.LeadDiscoveryProfileId == current.LeadDiscoveryProfileId
                        && e.Id != current.Id
                        && (e.Status == LeadDiscoveryExecutionStatus.Started
                            || e.Status == LeadDiscoveryExecutionStatus.Processing
                            || e.Status == LeadDiscoveryExecutionStatus.Retrying))
            .ToListAsync(cancellationToken);

        foreach (var execution in stale)
        {
            var neverAcquired = execution.LockStatus is LeadDiscoveryLockStatus.Pending or LeadDiscoveryLockStatus.Acquiring;

            if (LeadDiscoveryLockStateMachine.CanTransition(execution.LockStatus, LeadDiscoveryLockStatus.Expired))
            {
                try
                {
                    await _lockStore.RecordTransitionAsync(new LeadDiscoveryLockTransitionRecord(
                        execution.TenantId, execution.LeadDiscoveryProfileId, execution.Id, execution.LockTokenReference,
                        execution.LockOwnerInstanceId, execution.ProcessingDate, execution.LockStatus, LeadDiscoveryLockStatus.Expired,
                        _dateTime.UtcNow, $"Owner stopped without finishing; lease expired (found by execution {current.Id})", null,
                        execution.LockAcquiredAtUtc, execution.LockExpiresAtUtc, execution.LockLastRenewedAtUtc), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Could not record the expiry of stale lead discovery execution {ExecutionId}", execution.Id);
                }
            }

            await ApplyCustomerCountsAsync(execution, cancellationToken);
            if (execution.CampaignStatus == LeadDiscoveryCampaignStatus.Creating)
                execution.CampaignStatus = LeadDiscoveryCampaignStatus.RetryPending;
            if (execution.TemplateStatus == LeadDiscoveryAssociationStatus.Processing)
                execution.TemplateStatus = LeadDiscoveryAssociationStatus.RetryPending;
            if (execution.MappingStatus == LeadDiscoveryAssociationStatus.Processing)
                execution.MappingStatus = LeadDiscoveryAssociationStatus.RetryPending;

            execution.EndedAtUtc = _dateTime.UtcNow;
            execution.FailedStep ??= LeadDiscoverySteps.Interrupted;

            if (neverAcquired)
            {
                execution.Status = LeadDiscoveryExecutionStatus.Failed;
                execution.ErrorMessage = "The execution stopped before it acquired the lock. Nothing was processed.";
                execution.NextRetryInfo = "Not retryable - nothing was processed.";
            }
            else
            {
                execution.ErrorMessage ??= "The execution stopped without finishing and its lock lease expired. Committed work is kept.";
                ApplyRetryDecision(execution, anySuccess: execution.CustomersCreated + execution.CustomersSkipped > 0);
            }
        }

        if (stale.Count > 0)
            await _context.SaveChangesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------
    // Customers
    // ------------------------------------------------------------------------------------------------

    private async Task DiscoverCustomersAsync(ExecutionRun run, CancellationToken cancellationToken)
    {
        var profile = run.Profile;
        var tenantId = profile.TenantId;
        var stats = run.Stats;

        var rules = new QualificationRules(
            profile.PhoneRequired, profile.EmailRequired, profile.IndependentBusiness, profile.MinimumLeadScore, profile.RequiredFields);

        var knownBusinesses = await _context.DiscoveredLeads
            .Where(l => l.TenantId == tenantId)
            .OrderByDescending(l => l.CreatedAt)
            .Take(_options.MaxKnownBusinessesInPrompt)
            .Select(l => l.City == null ? l.BusinessName : l.BusinessName + ", " + l.City)
            .ToListAsync(cancellationToken);

        try
        {
            // A verification provider that is not answering ends the research too: more candidates would only be paid for and
            // left unchecked.
            // The run keeps researching until the batch the tenant asked for is full of customers - not until it has seen enough
            // candidates - and stops only when the research stalls (two rounds in a row find nothing new), the allowance runs out, or the
            // round limit is reached, which is what keeps a run's cost bounded.
            var emptyRounds = 0;
            var priorUnusableRate = await PriorUnusableRateAsync(profile, cancellationToken);

            // Leads an earlier run found and paid for but had no room for come first: no new research is bought for places they can fill.
            await UseCachedLeadsAsync(run, knownBusinesses, cancellationToken);

            while (stats.Created < run.BatchSize && stats.Rounds < _options.MaxRounds && !run.VerificationUnfinished && !stats.StoppedEarlyForAllowance)
            {
                run.Step = LeadDiscoverySteps.Discovery;
                await run.Lease.EnsureCanProcessAsync(cancellationToken);

                // What the tenant can still pay for caps how many leads are taken below, not how many candidates are researched:
                // only leads that become customers are charged.
                var affordable = await _quota.GetAvailableAsync(tenantId, QuotaType.LeadCandidates, cancellationToken);
                if (affordable < 1)
                {
                    var shortfall = await _quota.GetShortfallAsync(tenantId, QuotaType.LeadCandidates, cancellationToken);
                    stats.QuotaExhausted = shortfall is null || shortfall.Kind == QuotaShortfallKind.InsufficientCredits;
                    stats.QuotaStopReason = shortfall?.Message;
                    if (stats.QuotaExhausted)
                        await NotifyQuotaExhaustedAsync(tenantId, cancellationToken);
                    else
                        stats.StoppedEarlyForAllowance = true;
                    break;
                }

                stats.Rounds++;
                var remaining = run.BatchSize - stats.Created;

                // Ask for what is likely to be needed, not a fixed multiple: how many candidates this tenant's searches usually throw away,
                // corrected by what this run's own rounds have turned up so far (rejected, duplicate, or kept without a customer).
                var expectedUnusable = CandidateRequestPlanner.ExpectedUnusableRate(
                    priorUnusableRate, stats.Candidates, stats.Rejected + stats.Duplicates + (stats.Saved - stats.Created));
                var requested = CandidateRequestPlanner.Candidates(remaining, expectedUnusable, _options.MaxCandidatesPerRound, _options.CandidateHeadroomFactor);
                _logger.LogInformation(
                    "Lead discovery round {Round} for tenant {TenantId}: asking for {Requested} candidates to fill {Remaining} places (expecting {Unusable:P0} to be unusable)",
                    stats.Rounds, tenantId, requested, remaining, expectedUnusable);

                LeadDiscoveryAgentResult result;
                try
                {
                    result = await _agent.DiscoverAsync(new LeadDiscoveryAgentRequest(
                        profile.TargetBusinessType,
                        profile.Keywords,
                        profile.Locations,
                        requested,
                        profile.RequiredFields,
                        profile.PhoneRequired,
                        profile.EmailRequired,
                        profile.IndependentBusiness,
                        profile.MinimumLeadScore,
                        profile.AdditionalCriteria,
                        knownBusinesses), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Research cannot be resumed - whatever earlier rounds committed stays, and the campaign steps
                    // still run for it. The next scheduled run researches again.
                    _logger.LogError(ex, "Lead discovery agent failed for execution {ExecutionId}", run.Execution.Id);
                    run.DiscoveryFailed = true;
                    SetError(run.Execution, LeadDiscoverySteps.Discovery, ex.Message, ex.ToString());
                    await _context.SaveChangesAsync(cancellationToken);
                    break;
                }

                if (!result.IsConfigured)
                {
                    run.NotConfiguredReason = result.NotConfiguredReason;
                    break;
                }

                stats.Model = result.Model;
                stats.Candidates += result.Candidates.Count;
                stats.Usage += result.Usage;
                stats.AddNote(result.Note);

                // Everything the agent returned is known from here on, rejected candidates included, so the next round is told to look
                // for other businesses instead of handing the same ones back.
                foreach (var candidate in result.Candidates)
                {
                    if (!string.IsNullOrWhiteSpace(candidate.BusinessName))
                        knownBusinesses.Add(string.IsNullOrWhiteSpace(candidate.City) ? candidate.BusinessName : $"{candidate.BusinessName}, {candidate.City}");
                }

                if (result.Candidates.Count == 0)
                {
                    if (++emptyRounds >= 2)
                        break;
                    continue;
                }

                emptyRounds = 0;

                var assessments = LeadQualification.AssessAll(result.Candidates, result.Evidence, rules);
                var rows = new List<LeadDiscoveryExecutionCustomer>();
                var qualified = new List<VerifiedLead>();
                var rejectedRows = new List<LeadDiscoveryExecutionCustomer>();

                // What the agent reported for each qualified lead, to keep on its row whatever becomes of it. Qualification produces a new
                // object per lead, so the original is found by reference.
                var reported = new Dictionary<VerifiedLead, DiscoveredBusinessCandidate>(ReferenceEqualityComparer.Instance);
                string? DetailsOf(VerifiedLead lead) =>
                    reported.TryGetValue(lead, out var original) ? LeadDiscoveryCandidateSnapshot.Serialize(original) : null;

                for (var i = 0; i < assessments.Count; i++)
                {
                    if (assessments[i].Lead is { } lead)
                    {
                        qualified.Add(lead);
                        reported[lead] = result.Candidates[i];
                        continue;
                    }

                    stats.Reject(assessments[i].RejectionReason!);
                    var candidate = result.Candidates[i];
                    var rejectedRow = NewRow(run.Execution, candidate.BusinessName, candidate.Phone, LeadDiscoveryCustomerStatus.Skipped,
                        $"Invalid: {assessments[i].RejectionReason} (charged)", isInvalid: true);
                    rejectedRow.CandidateDetailsJson = LeadDiscoveryCandidateSnapshot.Serialize(candidate);
                    rows.Add(rejectedRow);
                    rejectedRows.Add(rejectedRow);
                }

                var fresh = await RemoveDuplicatesAsync(tenantId, qualified, cancellationToken);
                var freshSet = new HashSet<VerifiedLead>(fresh, ReferenceEqualityComparer.Instance);
                foreach (var duplicate in qualified.Where(l => !freshSet.Contains(l)))
                {
                    stats.Duplicates++;
                    var duplicateRow = NewRow(run.Execution, duplicate.BusinessName, duplicate.Phone, LeadDiscoveryCustomerStatus.Duplicate,
                        "Matches an existing customer or discovered lead (not charged).");
                    duplicateRow.CandidateDetailsJson = DetailsOf(duplicate);
                    rows.Add(duplicateRow);
                }

                // Only as many leads as the batch still needs and the tenant can still pay for. Candidates rejected by the profile's rules are
                // charged one lead's price each, like the leads saved (see ChargeLeadAsync), so what they cost comes off what can be spent on
                // saving leads. Duplicates are on this execution as Duplicate rows and are not charged.
                var spendable = Math.Max(0, (int)Math.Floor(affordable) - rejectedRows.Count);
                var toSave = fresh.Take(Math.Min(remaining, spendable)).ToList();

                // The qualified candidates beyond what the batch needs are recorded too, so every candidate the run considered is on the
                // execution with what became of it. They are not saved as customers and not charged; they are kept in the cache, because the
                // research that found them is already paid for, and the profile's next run uses them before researching anything new.
                var surplusLeads = fresh.Skip(toSave.Count).ToList();
                foreach (var surplus in surplusLeads)
                {
                    var surplusRow = NewRow(run.Execution, surplus.BusinessName, surplus.Phone, LeadDiscoveryCustomerStatus.Skipped, NotNeededNote);
                    surplusRow.CandidateDetailsJson = DetailsOf(surplus);
                    rows.Add(surplusRow);
                }

                await CacheSurplusAsync(run, surplusLeads, DetailsOf, cancellationToken);

                // Recorded Pending, with the candidate, before any of them is processed: an execution that stops
                // part-way leaves exactly the unprocessed ones for a retry.
                var pending = toSave.Select(lead =>
                {
                    var pendingRow = NewPendingRow(run.Execution, lead);
                    pendingRow.CandidateDetailsJson = DetailsOf(lead);
                    return (Lead: lead, Row: pendingRow);
                }).ToList();
                rows.AddRange(pending.Select(p => p.Row));
                _context.LeadDiscoveryExecutionCustomers.AddRange(rows);
                await _context.SaveChangesAsync(cancellationToken);

                // The rejected candidates are on the execution now, so each is charged against its own row (a retry cannot charge it twice).
                foreach (var rejectedRow in rejectedRows)
                    await ChargeLeadAsync(run, rejectedRow, rejectedRow.CustomerName, cancellationToken);

                foreach (var (lead, outcome) in await VerifyAndProcessAsync(run, pending, cancellationToken))
                {
                    if (outcome is CustomerOutcome.Created or CustomerOutcome.LeadOnly)
                    {
                        stats.Saved++;
                        knownBusinesses.Add(lead.City is null ? lead.BusinessName : $"{lead.BusinessName}, {lead.City}");
                    }

                    if (outcome == CustomerOutcome.Created)
                        stats.Created++;
                }
            }
        }
        finally
        {
            await RecordRunCostAsync(run);
        }
    }

    /// <summary>How long a kept lead stays offered to the profile's runs; after that its details are likely out of date.</summary>
    private const int CachedLeadLifetimeDays = 30;

    /// <summary>The most unused leads kept for one profile, so the cache cannot grow without bound.</summary>
    private const int CachedLeadsMaxPerProfile = 200;

    /// <summary>
    /// Keeps the qualified leads the batch had no room for, so the next run can use what the tenant already paid to find. A lead already
    /// in the cache is not added twice.
    /// </summary>
    private async Task CacheSurplusAsync(
        ExecutionRun run, IReadOnlyList<VerifiedLead> surplus, Func<VerifiedLead, string?> detailsOf, CancellationToken cancellationToken)
    {
        if (surplus.Count == 0)
            return;

        var profile = run.Profile;
        var now = _dateTime.UtcNow;
        var nameKeys = surplus.Select(l => l.NameKey).Distinct().ToList();
        var phoneKeys = surplus.Select(l => l.PhoneKey).OfType<string>().Distinct().ToList();

        var held = await _context.LeadDiscoveryCachedLeads.AsNoTracking()
            .Where(c => c.TenantId == profile.TenantId && c.LeadDiscoveryProfileId == profile.Id && c.UsedAtUtc == null && c.ExpiresAtUtc > now)
            .Select(c => new { c.NameKey, c.PhoneKey })
            .ToListAsync(cancellationToken);

        var heldNames = held.Select(h => h.NameKey).ToHashSet();
        var heldPhones = held.Select(h => h.PhoneKey).OfType<string>().ToHashSet();
        var room = CachedLeadsMaxPerProfile - held.Count;

        foreach (var lead in surplus)
        {
            if (room <= 0)
                break;
            if (heldNames.Contains(lead.NameKey) || (lead.PhoneKey is not null && heldPhones.Contains(lead.PhoneKey)))
                continue;

            _context.LeadDiscoveryCachedLeads.Add(new LeadDiscoveryCachedLead
            {
                TenantId = profile.TenantId,
                LeadDiscoveryProfileId = profile.Id,
                SourceExecutionId = run.Execution.Id,
                BusinessName = Truncate(lead.BusinessName, 300)!,
                PhoneKey = Truncate(lead.PhoneKey, 50),
                NameKey = Truncate(lead.NameKey, 500)!,
                CandidateJson = JsonSerializer.Serialize(lead),
                CandidateDetailsJson = detailsOf(lead),
                ExpiresAtUtc = now.AddDays(CachedLeadLifetimeDays)
            });
            heldNames.Add(lead.NameKey);
            if (lead.PhoneKey is not null)
                heldPhones.Add(lead.PhoneKey);
            room--;
        }
    }

    /// <summary>
    /// Takes the leads kept from earlier runs for as many places as the batch has (and the tenant can still pay for), before any new research.
    /// A kept lead that has since become a customer or discovered lead, or no longer fits the profile, is dropped. The leads taken go through
    /// the same verification, customer creation and charging as a freshly discovered one - they are charged only now, when they become customers.
    /// </summary>
    private async Task UseCachedLeadsAsync(ExecutionRun run, List<string> knownBusinesses, CancellationToken cancellationToken)
    {
        var profile = run.Profile;
        var tenantId = profile.TenantId;
        var stats = run.Stats;
        var now = _dateTime.UtcNow;

        var entries = await _context.LeadDiscoveryCachedLeads
            .Where(c => c.TenantId == tenantId && c.LeadDiscoveryProfileId == profile.Id && c.UsedAtUtc == null && c.ExpiresAtUtc > now)
            .OrderBy(c => c.CreatedAt)
            .Take(CachedLeadsMaxPerProfile)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
            return;

        bool StillFits(VerifiedLead lead) =>
            lead.LeadScore >= profile.MinimumLeadScore
            && (!profile.PhoneRequired || lead.PhoneE164 is not null)
            && (!profile.EmailRequired || !string.IsNullOrWhiteSpace(lead.Email));

        var byLead = new Dictionary<VerifiedLead, LeadDiscoveryCachedLead>(ReferenceEqualityComparer.Instance);
        var candidates = new List<VerifiedLead>();
        foreach (var entry in entries)
        {
            if (DeserializeCandidate(entry.CandidateJson) is { } lead && StillFits(lead))
            {
                byLead[lead] = entry;
                candidates.Add(lead);
            }
            else
            {
                entry.UsedAtUtc = now; // dropped
            }
        }

        var fresh = await RemoveDuplicatesAsync(tenantId, candidates, cancellationToken);
        var freshSet = new HashSet<VerifiedLead>(fresh, ReferenceEqualityComparer.Instance);
        foreach (var lead in candidates.Where(l => !freshSet.Contains(l)))
            byLead[lead].UsedAtUtc = now; // dropped: already a customer or discovered lead

        var affordable = await _quota.GetAvailableAsync(tenantId, QuotaType.LeadCandidates, cancellationToken);
        var toUse = fresh.Take(Math.Max(0, Math.Min(run.BatchSize - stats.Created, (int)Math.Floor(affordable)))).ToList();

        // Whatever is not used stays in the cache for the next run. The ones still held are known, so the agent is not sent to find them again.
        foreach (var lead in fresh.Skip(toUse.Count))
            knownBusinesses.Add(lead.City is null ? lead.BusinessName : $"{lead.BusinessName}, {lead.City}");

        if (toUse.Count == 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        run.Step = LeadDiscoverySteps.Customers;
        var pending = toUse.Select(lead =>
        {
            var entry = byLead[lead];
            entry.UsedAtUtc = now;
            entry.UsedExecutionId = run.Execution.Id;
            var row = NewPendingRow(run.Execution, lead);
            row.CandidateDetailsJson = entry.CandidateDetailsJson;
            return (Lead: lead, Row: row);
        }).ToList();
        _context.LeadDiscoveryExecutionCustomers.AddRange(pending.Select(p => p.Row));
        await _context.SaveChangesAsync(cancellationToken);

        stats.AddNote($"Used {toUse.Count} lead(s) kept from earlier runs - no new research was paid for them.");

        foreach (var (lead, outcome) in await VerifyAndProcessAsync(run, pending, cancellationToken))
        {
            if (outcome is CustomerOutcome.Created or CustomerOutcome.LeadOnly)
            {
                stats.Saved++;
                knownBusinesses.Add(lead.City is null ? lead.BusinessName : $"{lead.BusinessName}, {lead.City}");
            }

            if (outcome == CustomerOutcome.Created)
                stats.Created++;
        }
    }

    /// <summary>A retry's customers: what the predecessor already created is skipped, what it left Pending or
    /// Failed is processed again from its stored candidate. Nothing is researched again.</summary>
    private async Task ResumeCustomersAsync(ExecutionRun run, LeadDiscoveryExecution previous, CancellationToken cancellationToken)
    {
        run.Step = LeadDiscoverySteps.Customers;
        var now = _dateTime.UtcNow;

        var previousRows = await _context.LeadDiscoveryExecutionCustomers.AsNoTracking()
            .Where(r => r.ExecutionId == previous.Id && r.TenantId == previous.TenantId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        var toProcess = new List<(VerifiedLead Lead, LeadDiscoveryExecutionCustomer Row)>();
        var rows = new List<LeadDiscoveryExecutionCustomer>();

        foreach (var earlier in previousRows)
        {
            var alreadyCreated = earlier.Status == LeadDiscoveryCustomerStatus.Created
                                 || (earlier.Status == LeadDiscoveryCustomerStatus.Skipped && !earlier.IsInvalid && earlier.CustomerId is not null);
            if (alreadyCreated)
            {
                var skipped = NewRow(run.Execution, earlier.CustomerName, earlier.Phone, LeadDiscoveryCustomerStatus.Skipped,
                    "Already created by an earlier execution - not processed again.");
                skipped.RetriedFromId = earlier.Id;
                skipped.CustomerId = earlier.CustomerId;
                skipped.DiscoveredLeadId = earlier.DiscoveredLeadId;
                skipped.ProcessedAtUtc = now;
                // What the earlier execution learned about the number carries over.
                skipped.VerificationStatus = earlier.VerificationStatus;
                skipped.VerificationReasonCode = earlier.VerificationReasonCode;
                skipped.VerificationReason = earlier.VerificationReason;
                skipped.VerifiedAtUtc = earlier.VerifiedAtUtc;
                skipped.VerificationRequestId = earlier.VerificationRequestId;
                skipped.CandidateDetailsJson = earlier.CandidateDetailsJson;
                rows.Add(skipped);
                continue;
            }

            var unfinished = earlier.Status is LeadDiscoveryCustomerStatus.Pending or LeadDiscoveryCustomerStatus.Processing
                or LeadDiscoveryCustomerStatus.Failed;
            if (!unfinished || DeserializeCandidate(earlier.CandidateJson) is not { } lead)
                continue;

            var row = NewPendingRow(run.Execution, lead);
            row.RetriedFromId = earlier.Id;
            row.CandidateDetailsJson = earlier.CandidateDetailsJson;
            rows.Add(row);
            toProcess.Add((lead, row));
        }

        _context.LeadDiscoveryExecutionCustomers.AddRange(rows);
        await _context.SaveChangesAsync(cancellationToken);

        // An execution that began before leads were charged one by one paid for every candidate when it was researched (operation keys
        // "lead:{root}:r{round}"), so a retry of one of those must not charge its customers a second time.
        var legacyKey = $"lead:{run.Execution.RootExecutionId:N}:r";
        run.LeadsAlreadyBilled = await _context.QuotaLedgerEntries.AsNoTracking()
            .AnyAsync(e => e.TenantId == run.Profile.TenantId && e.OperationKey != null && e.OperationKey.StartsWith(legacyKey), cancellationToken);

        await VerifyAndProcessAsync(run, toProcess, cancellationToken);
    }

    /// <summary>
    /// Checks the numbers first, then creates customers only for the ones that passed. A number the provider rejected (DND and the
    /// like) never becomes a customer - its business is kept as a discovered lead and its row records the rejection, so the tenant
    /// still sees what the discovery cost and found. A number that could not be checked is left Pending, with its candidate, for a
    /// retry to ask again. A business with no usable number is not checked and is handled as before.
    /// </summary>
    private async Task<List<(VerifiedLead Lead, CustomerOutcome Outcome)>> VerifyAndProcessAsync(
        ExecutionRun run, IReadOnlyList<(VerifiedLead Lead, LeadDiscoveryExecutionCustomer Row)> items, CancellationToken cancellationToken)
    {
        // The profile's DND-check switch (off by default): when off nothing is submitted, and every qualified lead is processed as
        // it always was.
        var dndCheck = run.Profile.DndCheckEnabled;
        if (dndCheck)
            await VerifyNumbersAsync(run, items, cancellationToken);

        run.Step = LeadDiscoverySteps.Customers;
        var outcomes = new List<(VerifiedLead, CustomerOutcome)>();
        foreach (var (lead, row) in items)
        {
            await run.Lease.EnsureCanProcessAsync(cancellationToken);

            if (dndCheck && lead.PhoneE164 is not null
                && row.VerificationStatus is not (LeadDiscoveryVerificationStatus.Pass or LeadDiscoveryVerificationStatus.Fail))
                continue;

            // Hard stop at the plan's customer limit, for every path that makes customers (a retry resumes candidates found earlier): what is
            // left stays Pending, with its candidate, for a run after the tenant has room.
            if (lead.PhoneE164 is not null && await CustomerQuota.GetAsync(_context, run.Profile.TenantId, cancellationToken) is { Exhausted: true } full)
            {
                run.Stats.QuotaStopReason ??= CustomerQuota.LimitReachedMessage(full);
                run.Stats.StoppedEarlyForAllowance = true;
                break;
            }

            var outcome = await ProcessCustomerAsync(run, row, lead, cancellationToken);
            outcomes.Add((lead, outcome));

            // Charged the moment the lead is committed, so a run that stops part-way has charged exactly what it saved. A lead kept without a
            // customer (no usable number, or a number the DND check refused) is charged like a customer: the research found a real business,
            // and the tenant keeps it as a discovered lead.
            if (outcome is CustomerOutcome.Created or CustomerOutcome.LeadOnly)
                await ChargeLeadAsync(run, row, lead.BusinessName, cancellationToken);
        }

        return outcomes;
    }

    /// <summary>
    /// The one place a tenant pays for discovery: one lead's price for each customer a run creates, and for each business it keeps only as a
    /// discovered lead (no usable number, or a number the DND check refused), and for each candidate rejected by the profile's rules. A
    /// duplicate, or a surplus lead kept for the next run, is not charged. The operation key is the history row's id, so a retry that meets the same customer cannot charge it twice.
    /// Never fails the run - the customer is already committed - and a wallet that has run dry since the batch was planned simply
    /// charges what is left.
    /// </summary>
    private async Task ChargeLeadAsync(ExecutionRun run, LeadDiscoveryExecutionCustomer row, string businessName, CancellationToken cancellationToken)
    {
        if (run.LeadsAlreadyBilled)
            return;

        try
        {
            var charged = await _quota.ConsumeLeadCandidatesAsync(
                run.Profile.TenantId,
                1,
                $"lead:{row.Id:N}",
                run.Execution.Id.ToString("N"),
                $"Lead: {Truncate(businessName, 120)}",
                cancellationToken);

            if (charged > 0)
                run.Stats.Billed++;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not charge lead discovery customer {CustomerName} in execution {ExecutionId}", row.CustomerName, run.Execution.Id);
        }

        // The quota ledger resets the change tracker when it retries a concurrency conflict.
        _context.LeadDiscoveryExecutions.Attach(run.Execution);
    }

    /// <summary>
    /// How many candidates this profile's recent real searches threw away (rejected or duplicate), as a share of all they considered - the
    /// starting estimate for how many to ask for. What gets thrown away depends on the profile's rules (a required contact person, a required
    /// phone, ...), so it learns only from this profile's own runs, and only from those since the profile was last changed: a run made under
    /// earlier rules says nothing about the current ones. Simulated runs are left out because their businesses are invented. With too little
    /// history the configured starting guess is used.
    /// </summary>
    private async Task<decimal> PriorUnusableRateAsync(LeadDiscoveryProfile profile, CancellationToken cancellationToken)
    {
        var since = profile.UpdatedAt ?? profile.CreatedAt;

        // "Simulated" is what a simulated run records as its model (SimulatedLeadDiscoveryAgent.ModelName, in Infrastructure).
        var recent = await (
                from r in _context.LeadDiscoveryRuns.AsNoTracking()
                join e in _context.LeadDiscoveryExecutions.AsNoTracking() on r.ExecutionId equals e.Id
                where r.TenantId == profile.TenantId
                      && e.LeadDiscoveryProfileId == profile.Id
                      && r.RanAtUtc >= since
                      && r.CandidatesConsidered > 0
                      && r.Model != "Simulated"
                orderby r.RanAtUtc descending
                select new { r.CandidatesConsidered, r.Rejected, r.Duplicates })
            .Take(RecentRunsForRejectionRate)
            .ToListAsync(cancellationToken);

        var learned = CandidateRequestPlanner.RateFromHistory(
            recent.Select(r => (r.CandidatesConsidered, r.Rejected, r.Duplicates)), MinCandidatesForRejectionRate);

        return learned ?? Math.Clamp(_options.InitialRejectionRatePercent, 0, 90) / 100m;
    }

    /// <summary>How many of the tenant's latest runs the unusable-candidate rate is learned from, and the fewest candidates that is allowed to
    /// rest on: a couple of runs of a handful of businesses say nothing, so the starting guess is used until there is more.</summary>
    private const int RecentRunsForRejectionRate = 5;

    private const int MinCandidatesForRejectionRate = 10;

    /// <summary>
    /// Submits the rows' numbers to the verification provider as one batch and records each answer on its row, matched by the row's
    /// id - never by order or by number. A provider failure or a missing answer is recorded as Error, never as Pass, and does not
    /// stop the run; the execution stays retryable.
    /// </summary>
    private async Task VerifyNumbersAsync(
        ExecutionRun run, IReadOnlyList<(VerifiedLead Lead, LeadDiscoveryExecutionCustomer Row)> items, CancellationToken cancellationToken)
    {
        run.Step = LeadDiscoverySteps.Verification;
        await run.Lease.EnsureCanProcessAsync(cancellationToken);

        var targets = items
            .Where(i => i.Lead.PhoneE164 is not null
                        && i.Row.VerificationStatus is LeadDiscoveryVerificationStatus.NotChecked
                            or LeadDiscoveryVerificationStatus.Pending or LeadDiscoveryVerificationStatus.Error)
            .ToList();
        if (targets.Count == 0)
            return;

        PhoneVerificationResponse? response = null;
        string? providerFailure = null;
        try
        {
            response = await _verification.VerifyAsync(
                run.Profile.TenantId,
                targets.Select(t => new PhoneVerificationItem(t.Row.Id, t.Lead.PhoneE164!)).ToList(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Phone verification failed for lead discovery execution {ExecutionId}", run.Execution.Id);
            providerFailure = Truncate(ex.Message, 400);
        }

        var answers = new Dictionary<Guid, PhoneVerificationItemResult>();
        foreach (var result in response?.Results ?? Array.Empty<PhoneVerificationItemResult>())
            answers.TryAdd(result.ReferenceId, result);

        var now = _dateTime.UtcNow;
        foreach (var (_, row) in targets)
        {
            LeadDiscoveryVerificationStatus status;
            string? code = null, reason = null;
            if (answers.TryGetValue(row.Id, out var answer))
            {
                status = answer.Status == LeadDiscoveryVerificationStatus.NotChecked
                    ? LeadDiscoveryVerificationStatus.Error : answer.Status;
                code = answer.ReasonCode;
                reason = answer.Reason;
            }
            else
            {
                status = LeadDiscoveryVerificationStatus.Error;
                code = providerFailure is null ? "NO_RESULT" : "PROVIDER_ERROR";
                reason = providerFailure ?? "The provider returned no result for this number.";
            }

            row.VerificationStatus = status;
            row.VerificationReasonCode = Truncate(code, 50);
            row.VerificationReason = Truncate(reason, 500);
            row.VerificationRequestId = Truncate(response?.RequestId, 100);
            row.VerifiedAtUtc = status == LeadDiscoveryVerificationStatus.Pending ? null : now;
            if (status is LeadDiscoveryVerificationStatus.Error or LeadDiscoveryVerificationStatus.Pending)
                run.VerificationUnfinished = true;
        }

        if (run.VerificationUnfinished)
        {
            var unfinished = targets.Count(t => t.Row.VerificationStatus is LeadDiscoveryVerificationStatus.Error or LeadDiscoveryVerificationStatus.Pending);
            SetError(run.Execution, LeadDiscoverySteps.Verification,
                $"{unfinished} number(s) could not be verified; no customer is created for them until a retry succeeds.", null);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One customer, one transaction: duplicate re-check, then DiscoveredLead + Customer inserted and the history
    /// row marked Created - committed together. On failure the transaction rolls back, and the row is recorded
    /// Failed (keeping its candidate for a retry) outside it. Customers committed before are unaffected.
    /// </summary>
    private async Task<CustomerOutcome> ProcessCustomerAsync(
        ExecutionRun run, LeadDiscoveryExecutionCustomer row, VerifiedLead lead, CancellationToken cancellationToken)
    {
        var tenantId = run.Profile.TenantId;
        var candidateJson = row.CandidateJson;

        // A previous customer's rollback clears the change tracker; this row was saved Pending, so re-tracking it
        // as Unchanged is exact.
        _context.LeadDiscoveryExecutionCustomers.Attach(row);

        Exception? failure = null;
        await using (var transaction = await _context.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                var now = _dateTime.UtcNow;
                row.ProcessedAtUtc = now;

                // Re-checked inside the transaction: an earlier customer in this same batch, or another writer,
                // may have taken this business since the batch was filtered.
                if ((await RemoveDuplicatesAsync(tenantId, new[] { lead }, cancellationToken)).Count == 0)
                {
                    row.Status = LeadDiscoveryCustomerStatus.Duplicate;
                    row.ErrorMessage = "Matches an existing customer or discovered lead.";
                    row.CandidateJson = null;
                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return CustomerOutcome.Duplicate;
                }

                var discovered = ToEntity(tenantId, lead);
                _context.DiscoveredLeads.Add(discovered);
                row.DiscoveredLeadId = discovered.Id;
                row.CandidateJson = null;

                if (lead.PhoneE164 is not { } phoneE164)
                {
                    // Customer requires an E.164 number, so this business stays a discovery row only.
                    row.Status = LeadDiscoveryCustomerStatus.Skipped;
                    row.IsInvalid = true;
                    row.ErrorMessage = "Invalid: no WhatsApp-capable phone number - kept as a discovered lead only.";
                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return CustomerOutcome.LeadOnly;
                }

                if (row.VerificationStatus == LeadDiscoveryVerificationStatus.Fail)
                {
                    // Rejected by the verification provider (DND and the like): no customer is created, since nothing could be sent to
                    // it. The business stays as a discovered lead - so it is not paid for again - and this row is the record.
                    row.Status = LeadDiscoveryCustomerStatus.Skipped;
                    row.IsInvalid = true;
                    row.ErrorMessage = Truncate(
                        $"Rejected by the DND check{(row.VerificationReasonCode is null ? "" : $" ({row.VerificationReasonCode})")}: " +
                        $"{row.VerificationReason ?? "number not eligible for outreach"} - no customer created.",
                        ErrorMessageLength);
                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return CustomerOutcome.LeadOnly;
                }

                var customer = new Customer
                {
                    TenantId = tenantId,
                    PhoneNumberE164 = phoneE164,
                    // Customer is person-shaped, a discovered lead is a business: the named contact when the
                    // research found one, the business name otherwise, so the CRM row is recognisable either way.
                    FirstName = Truncate(lead.ContactPerson ?? lead.BusinessName, CustomerNameLength),
                    Email = Truncate(lead.Email, LeadDiscoveryLimits.Email),
                    Source = CustomerSource,
                    // PendingOptIn, or WaitingForConsent when the tenant requires consent (Customers page setting); no
                    // consent evidence is written either way -
                    // being discovered on the web is not consent to be messaged. Campaigns only ever send to
                    // OptedIn customers (see CampaignSendService), so nothing discovered here can be messaged
                    // until the customer replies or taps the opt-in link and messages the business first.
                    // There is deliberately no switch to skip this.
                    OptInStatus = await Customers.ConsentPolicy.InitialStatusAsync(_context, tenantId, cancellationToken)
                };
                // Every customer a run creates is tagged with the run's processing date, so a day's discoveries can be found
                // (and campaigned to) together in the Customers screen.
                customer.Tags.Add(await GetOrCreateDateTagAsync(tenantId, run.Execution.ProcessingDate, cancellationToken));
                _context.Customers.Add(customer);
                discovered.CustomerId = customer.Id;

                // In the same transaction as the customer, so a customer exists exactly when its "discovered" event does.
                _context.CustomerHistoryEvents.Add(new CustomerHistoryEvent
                {
                    TenantId = tenantId,
                    CustomerId = customer.Id,
                    EventType = CustomerHistoryEventTypes.Discovered,
                    // When the row was recorded - before the number was checked - so discovery precedes verification on the timeline.
                    OccurredAtUtc = row.CreatedAt == default ? now : row.CreatedAt,
                    NewStatus = "Discovered",
                    Reason = $"Found by lead discovery for \"{Truncate(run.Profile.TargetBusinessType, 200)}\".",
                    ExecutionId = run.Execution.Id,
                    CorrelationId = run.Execution.Id.ToString("N")
                });

                if (row.VerificationStatus == LeadDiscoveryVerificationStatus.Pass)
                {
                    var verifiedAt = row.VerifiedAtUtc ?? now;
                    foreach (var (type, previousStatus, newStatus) in new[]
                             {
                                 (CustomerHistoryEventTypes.VerificationSubmitted, "NotChecked", "Pending"),
                                 (CustomerHistoryEventTypes.VerificationPassed, "Pending", "Pass")
                             })
                    {
                        _context.CustomerHistoryEvents.Add(new CustomerHistoryEvent
                        {
                            TenantId = tenantId, CustomerId = customer.Id, EventType = type, OccurredAtUtc = verifiedAt,
                            PreviousStatus = previousStatus, NewStatus = newStatus,
                            ExecutionId = run.Execution.Id, CorrelationId = run.Execution.Id.ToString("N"),
                            ProviderReference = row.VerificationRequestId
                        });
                    }
                }

                row.CustomerId = customer.Id;
                row.Status = LeadDiscoveryCustomerStatus.Created;
                row.ErrorMessage = null;
                row.ErrorDetails = null;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return CustomerOutcome.Created;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex;
                await RollbackQuietlyAsync(transaction);
            }
        }

        _logger.LogWarning(failure, "Lead discovery customer {CustomerName} failed in execution {ExecutionId}", row.CustomerName, run.Execution.Id);

        RestoreTracking(run.Execution);
        row.Status = LeadDiscoveryCustomerStatus.Failed;
        row.CustomerId = null;
        row.DiscoveredLeadId = null;
        row.IsInvalid = false;
        row.CandidateJson = candidateJson;
        row.ErrorMessage = Truncate(failure!.Message, ErrorMessageLength);
        row.ErrorDetails = Truncate(failure.ToString(), ErrorDetailsLength);
        row.ProcessedAtUtc = _dateTime.UtcNow;
        _context.LeadDiscoveryExecutionCustomers.Update(row);
        await _context.SaveChangesAsync(cancellationToken);
        return CustomerOutcome.Failed;
    }

    /// <summary>The tenant's tag named for <paramref name="processingDate"/> (yyyy-MM-dd, the tenant's local date for this run), created on
    /// first use. Looked up per customer rather than cached: a rolled-back customer clears the change tracker, and a tag created inside
    /// that transaction would be gone with it.</summary>
    private async Task<CustomerTag> GetOrCreateDateTagAsync(Guid tenantId, DateTime processingDate, CancellationToken cancellationToken)
    {
        var name = DateTagName(processingDate);

        var tag = await _context.CustomerTags.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Name == name, cancellationToken);
        if (tag is null)
        {
            tag = new CustomerTag { TenantId = tenantId, Name = name };
            _context.CustomerTags.Add(tag);
        }

        return tag;
    }

    /// <summary>The name of the tag a run puts on the customers it creates: its processing date, e.g. 2026-10-02.</summary>
    internal static string DateTagName(DateTime processingDate) => processingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------------
    // Auto-Campaign
    // ------------------------------------------------------------------------------------------------

    private async Task ProcessAutoCampaignAsync(ExecutionRun run, CancellationToken cancellationToken)
    {
        var execution = run.Execution;
        var profile = run.Profile;
        run.Step = LeadDiscoverySteps.Campaign;

        var configured = profile.AutoCampaignEnabled && profile.SourceCampaignId is not null;
        execution.AutoCampaignConfigured = configured;
        execution.AutoCampaignId = configured ? profile.SourceCampaignId : null;
        execution.ReferredCampaignId = configured ? profile.SourceCampaignId : null;

        if (!configured)
        {
            SkipCampaign(execution, "Auto-Campaign = Not Configured");
            await _context.SaveChangesAsync(cancellationToken);

            // The consent SMS goes to the customers the campaign takes, so with no campaign nobody is texted - even with the switch on. Say
            // that on each customer, rather than leaving the screen to guess ("SMS consent is not configured" would be wrong: the switch is on).
            if (profile.ConsentSmsEnabled)
            {
                var created = await EligibleCustomerIdsAsync(execution, profile.DndCheckEnabled, cancellationToken);
                await RecordConsentSmsNotSentAsync(run, created.Passed, AutoCampaignNotConfiguredReason, cancellationToken);
            }

            return;
        }

        var customers = await EligibleCustomerIdsAsync(execution, profile.DndCheckEnabled, cancellationToken);
        var eligible = customers.Passed;
        execution.MappingsEligible = eligible.Count;

        if (eligible.Count == 0)
        {
            SkipCampaign(execution, customers.Rejected > 0
                ? $"None of the {customers.Rejected + customers.Created} discovered numbers passed the DND check. Campaign creation skipped."
                : "No new customers discovered/created. Campaign creation skipped.");
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        // Read-only: the referred campaign is the Auto-Campaign configuration and is never modified.
        var source = await _context.Campaigns.AsNoTracking()
            .Include(c => c.Steps).ThenInclude(s => s.StepMedia)
            .FirstOrDefaultAsync(c => c.Id == profile.SourceCampaignId && c.TenantId == profile.TenantId, cancellationToken);
        execution.ReferredCampaignName = source?.Name;

        var campaignId = await EnsureCampaignAsync(run, source, cancellationToken);
        if (campaignId is null)
            return;

        if (!await AssociateTemplatesAsync(run, source!, campaignId.Value, cancellationToken))
            return;

        if (!await MapCustomersAsync(run, campaignId.Value, eligible, cancellationToken))
            return;

        await ActivateCampaignAsync(run, campaignId.Value, cancellationToken);

        if (profile.ConsentSmsEnabled)
            await SendConsentInvitationsAsync(run, campaignId.Value, eligible, cancellationToken);
        else
            await RecordConsentSmsNotSentAsync(run, eligible, ConsentSmsOffReason, cancellationToken);
    }

    /// <summary>The note on a row for a candidate that qualified but was not needed because the batch was already full (see <see cref="LeadDiscoveryExecutionOutcomes"/>).</summary>
    internal const string NotNeededNote = "Qualified, but the batch was already full - kept for the next run, not charged.";

    /// <summary>The wording before leads were kept for the next run. History rows already saved carry it.</summary>
    internal const string LegacyNotNeededNote = "Qualified, but the batch was already full - not saved and not charged.";

    // What the Consent SMS column says when a customer was not sent the SMS because of a setting. There are two, and the first one decides: the
    // consent SMS goes only to the customers an Auto-Campaign takes, so with no Auto-Campaign that is what has to be fixed first. The headline is
    // shown in bold, ahead of what to do about it.

    /// <summary>No Auto-Campaign was configured, so no campaign took the customer (whether or not the consent SMS switch is on).</summary>
    internal const string AutoCampaignNotConfiguredHeadline = "Not added to a campaign: Auto-Campaign is not configured.";

    /// <summary>The reason recorded on each customer, and shown on the execution, when the run had no Auto-Campaign.</summary>
    internal const string AutoCampaignNotConfiguredReason = AutoCampaignNotConfiguredHeadline + " Turn on Auto-Campaign and choose a campaign in the discovery profile.";

    /// <summary>The Auto-Campaign took the customer, but the consent SMS is not set up (the switch in the discovery profile is off).</summary>
    internal const string ConsentSmsOffHeadline = "SMS consent is not configured.";

    /// <summary>The reason recorded on each customer's history, and shown on the execution, when the consent SMS is switched off.</summary>
    internal const string ConsentSmsOffReason = ConsentSmsOffHeadline + " Turn on \"Send WhatsApp consent SMS\" in the discovery profile to send it.";

    // The wording these replaced. History rows already saved carry it, so it is still recognised and shown as the current wording.
    internal const string LegacyAutoCampaignNotConfiguredReason =
        "Auto-Campaign is not configured, so no campaign took this customer and no consent SMS was sent. Turn on Auto-Campaign and choose a campaign in the discovery profile.";
    internal const string LegacyConsentSmsOffReason =
        "The consent SMS was Off, so no invitation was sent. Turn on \"Send WhatsApp consent SMS\" in the discovery profile to send it.";

    /// <summary>
    /// When no consent SMS goes to a customer - the switch is off, or no campaign took them - each still gets one history entry saying no SMS
    /// was sent and why: an empty column would not tell the tenant whether something went wrong. Once per customer per execution, so a retried
    /// execution adds none.
    /// </summary>
    private async Task RecordConsentSmsNotSentAsync(ExecutionRun run, IReadOnlyCollection<Guid> customerIds, string reason, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
            return;

        var executionId = run.Execution.Id;
        var already = (await _context.CustomerHistoryEvents.AsNoTracking()
                .Where(e => e.ExecutionId == executionId && e.EventType == CustomerHistoryEventTypes.SmsSkipped && customerIds.Contains(e.CustomerId))
                .Select(e => e.CustomerId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var now = _dateTime.UtcNow;
        foreach (var customerId in customerIds.Where(id => !already.Contains(id)))
        {
            _context.CustomerHistoryEvents.Add(new CustomerHistoryEvent
            {
                TenantId = run.Profile.TenantId,
                CustomerId = customerId,
                EventType = CustomerHistoryEventTypes.SmsSkipped,
                OccurredAtUtc = now,
                NewStatus = "NotSent",
                Reason = reason,
                CampaignId = run.Execution.GeneratedCampaignId,
                ExecutionId = executionId,
                CorrelationId = executionId.ToString("N")
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Texts each customer now in the campaign an invitation to start a WhatsApp conversation - the way their consent is obtained. The
    /// customers are exactly those the campaign took, so a number that failed the DND check is never texted. Idempotent: one invitation
    /// exists per (customer, campaign), so a retried execution only reaches customers who have none yet. A failure here is recorded on
    /// each invitation and never fails the execution.
    /// </summary>
    private async Task SendConsentInvitationsAsync(ExecutionRun run, Guid campaignId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        run.Step = LeadDiscoverySteps.ConsentSms;
        await run.Lease.EnsureCanProcessAsync(cancellationToken);

        try
        {
            var result = await _consent.SendAsync(campaignId, run.Execution.Id, customerIds, cancellationToken);
            _logger.LogInformation(
                "Consent invitations for execution {ExecutionId}: {Sent} sent, {Failed} failed, {Skipped} skipped",
                run.Execution.Id, result.Sent, result.Failed, result.Skipped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not LeadDiscoveryLockUnavailableException)
        {
            _logger.LogWarning(ex, "Sending consent invitations failed for lead discovery execution {ExecutionId}", run.Execution.Id);
        }
    }

    private static void SkipCampaign(LeadDiscoveryExecution execution, string note)
    {
        execution.CampaignStatus = LeadDiscoveryCampaignStatus.Skipped;
        execution.TemplateStatus = LeadDiscoveryAssociationStatus.Skipped;
        execution.MappingStatus = LeadDiscoveryAssociationStatus.Skipped;
        execution.CampaignNote = note;
    }

    /// <summary>Customers created anywhere in this execution's retry chain that still exist in this tenant - never
    /// a duplicate, never another tenant's, never one from an unrelated execution.</summary>
    private async Task<EligibleCustomers> EligibleCustomerIdsAsync(
        LeadDiscoveryExecution execution, bool requireVerificationPass, CancellationToken cancellationToken)
    {
        var tenantId = execution.TenantId;
        var rootId = execution.RootExecutionId;
        var chain = _context.LeadDiscoveryExecutions
            .Where(e => e.RootExecutionId == rootId && e.TenantId == tenantId)
            .Select(e => e.Id);

        var created = await _context.LeadDiscoveryExecutionCustomers.AsNoTracking()
            .Where(r => chain.Contains(r.ExecutionId) && r.TenantId == tenantId
                        && r.Status == LeadDiscoveryCustomerStatus.Created && r.CustomerId != null)
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.CustomerId!.Value)
            .ToListAsync(cancellationToken);

        var existing = (await _context.Customers.AsNoTracking()
                .Where(c => c.TenantId == tenantId && created.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var all = created.Where(existing.Contains).Distinct().ToList();

        // With the DND check off nothing was verified and every created customer is eligible, as before the check existed.
        if (!requireVerificationPass)
            return new EligibleCustomers(all, all.Count, 0);

        // With it on, a customer is only ever created for a number that passed, so this keeps out anything that did not (a
        // customer from before verification existed has no Pass on record and is left out too).
        var passed = (await _context.LeadDiscoveryExecutionCustomers.AsNoTracking()
                .Where(r => chain.Contains(r.ExecutionId) && r.TenantId == tenantId && r.CustomerId != null
                            && r.VerificationStatus == LeadDiscoveryVerificationStatus.Pass)
                .Select(r => r.CustomerId!.Value)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var rejected = await _context.LeadDiscoveryExecutionCustomers.AsNoTracking()
            .CountAsync(r => chain.Contains(r.ExecutionId) && r.TenantId == tenantId
                             && r.VerificationStatus == LeadDiscoveryVerificationStatus.Fail, cancellationToken);

        return new EligibleCustomers(all.Where(passed.Contains).ToList(), all.Count, rejected);
    }

    /// <param name="Passed">Created customers whose number passed verification, in discovery order.</param>
    /// <param name="Created">All customers created in the chain.</param>
    /// <param name="Rejected">Numbers the provider rejected in the chain - no customer exists for them.</param>
    private sealed record EligibleCustomers(List<Guid> Passed, int Created, int Rejected);

    /// <summary>The campaign for (tenant, profile, auto campaign, processing date): reused when it already exists,
    /// created in its own transaction when it does not. Returns null when the step failed.</summary>
    private async Task<Guid?> EnsureCampaignAsync(ExecutionRun run, Campaign? source, CancellationToken cancellationToken)
    {
        var execution = run.Execution;
        run.Step = LeadDiscoverySteps.Campaign;
        await run.Lease.EnsureCanProcessAsync(cancellationToken);

        execution.CampaignStatus = LeadDiscoveryCampaignStatus.Creating;
        await _context.SaveChangesAsync(cancellationToken);

        if (source is null)
            return await FailCampaignAsync(execution, "The referred campaign no longer exists.", null, cancellationToken);
        if (source.Status == CampaignStatus.Stopped)
            return await FailCampaignAsync(execution, $"The referred campaign '{source.Name}' is Stopped.", null, cancellationToken);

        if (source.IsRetired)
            return await FailCampaignAsync(execution, $"The referred campaign '{source.Name}' was retired.", null, cancellationToken);

        var existing = await FindGeneratedCampaignAsync(execution, source.Id, cancellationToken);
        // A generated campaign the tenant has since deleted or retired is not reused: its mapping is replaced by a new
        // campaign below, so the run recovers by itself instead of failing on every retry.
        LeadDiscoveryGeneratedCampaign? stale = null;
        if (existing is not null)
        {
            var stillExists = await _context.Campaigns.AsNoTracking()
                .AnyAsync(c => c.Id == existing.CampaignId && c.TenantId == execution.TenantId && c.RetiredAt == null, cancellationToken);
            if (stillExists)
                return await ReuseCampaignAsync(execution, existing, cancellationToken);

            stale = existing;
        }

        var campaign = new Campaign
        {
            TenantId = execution.TenantId,
            Name = BuildCampaignName(source.Name, execution.ProcessingDate),
            Description = source.Description,
            Status = CampaignStatus.Draft,
            // The profile's own choice, not the referred campaign's schedule - see
            // LeadDiscoveryProfile.AutoCampaignStartMode.
            ScheduledStartAt = run.Profile.AutoCampaignStartMode == LeadDiscoveryCampaignStartMode.NextDayWithTime
                && run.Profile.AutoCampaignStartTime is { } startTime
                ? execution.ProcessingDate.Date.AddDays(1) + startTime
                : null,
            CreatedBy = source.CreatedBy,
            TargetAudienceFilterJson = JsonSerializer.Serialize(new
            {
                source = "LeadDiscovery",
                leadDiscoveryProfileId = execution.LeadDiscoveryProfileId,
                referredCampaignId = source.Id,
                processingDate = execution.ProcessingDate.ToString("yyyy-MM-dd")
            })
        };

        Exception? failure = null;
        await using (var transaction = await _context.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await _planLimits.EnsureCanCreateCampaignAsync(execution.TenantId, cancellationToken);

                if (stale is not null)
                {
                    // Saved on its own first: EF does not order a delete ahead of an insert for the same unique key.
                    // Loaded tracked: the context may already be tracking this row from an earlier step.
                    var tracked = await _context.LeadDiscoveryGeneratedCampaigns.FirstOrDefaultAsync(g => g.Id == stale.Id, cancellationToken);
                    if (tracked is not null)
                        _context.LeadDiscoveryGeneratedCampaigns.Remove(tracked);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                _context.Campaigns.Add(campaign);
                _context.LeadDiscoveryGeneratedCampaigns.Add(new LeadDiscoveryGeneratedCampaign
                {
                    TenantId = execution.TenantId,
                    LeadDiscoveryProfileId = execution.LeadDiscoveryProfileId,
                    AutoCampaignId = source.Id,
                    ProcessingDate = execution.ProcessingDate,
                    CampaignId = campaign.Id,
                    ExecutionId = execution.Id
                });

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex;
                await RollbackQuietlyAsync(transaction);
            }
        }

        if (failure is null)
        {
            execution.GeneratedCampaignId = campaign.Id;
            execution.GeneratedCampaignName = campaign.Name;
            execution.CampaignStatus = LeadDiscoveryCampaignStatus.Created;
            execution.CampaignNote = stale is null
                ? "Campaign created."
                : $"Campaign created - the one generated earlier for {execution.ProcessingDate:yyyy-MM-dd} ({stale.CampaignId}) had been deleted or retired.";
            await _context.SaveChangesAsync(cancellationToken);
            return campaign.Id;
        }

        RestoreTracking(execution);

        // Losing a creation race surfaces as a unique-key violation: the other side's campaign is the one to use.
        var winner = await FindGeneratedCampaignAsync(execution, source.Id, cancellationToken);
        if (winner is not null)
            return await ReuseCampaignAsync(execution, winner, cancellationToken);

        _logger.LogWarning(failure, "Campaign creation failed for lead discovery execution {ExecutionId}", execution.Id);
        return await FailCampaignAsync(execution, failure.Message, failure.ToString(), cancellationToken);
    }

    private Task<LeadDiscoveryGeneratedCampaign?> FindGeneratedCampaignAsync(
        LeadDiscoveryExecution execution, Guid autoCampaignId, CancellationToken cancellationToken) =>
        _context.LeadDiscoveryGeneratedCampaigns.AsNoTracking()
            .FirstOrDefaultAsync(g => g.TenantId == execution.TenantId
                                      && g.LeadDiscoveryProfileId == execution.LeadDiscoveryProfileId
                                      && g.AutoCampaignId == autoCampaignId
                                      && g.ProcessingDate == execution.ProcessingDate, cancellationToken);

    private async Task<Guid?> ReuseCampaignAsync(
        LeadDiscoveryExecution execution, LeadDiscoveryGeneratedCampaign generated, CancellationToken cancellationToken)
    {
        var campaign = await _context.Campaigns.AsNoTracking()
            .Where(c => c.Id == generated.CampaignId && c.TenantId == execution.TenantId)
            .Select(c => new { c.Id, c.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (campaign is null)
            return await FailCampaignAsync(execution,
                $"The campaign generated for this profile on {execution.ProcessingDate:yyyy-MM-dd} ({generated.CampaignId}) no longer exists.",
                null, cancellationToken);

        execution.GeneratedCampaignId = campaign.Id;
        execution.GeneratedCampaignName = campaign.Name;
        execution.CampaignStatus = LeadDiscoveryCampaignStatus.Created;
        execution.CampaignNote = generated.ExecutionId == execution.Id
            ? "Campaign created."
            : $"Reused the campaign already generated for this profile on {execution.ProcessingDate:yyyy-MM-dd}.";
        await _context.SaveChangesAsync(cancellationToken);
        return campaign.Id;
    }

    private async Task<Guid?> FailCampaignAsync(LeadDiscoveryExecution execution, string message, string? details, CancellationToken cancellationToken)
    {
        execution.CampaignStatus = LeadDiscoveryCampaignStatus.RetryPending;
        execution.CampaignNote = Truncate(message, 1000);
        execution.GeneratedCampaignId = null;
        execution.GeneratedCampaignName = null;
        SetError(execution, LeadDiscoverySteps.Campaign, message, details);
        await _context.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <summary>
    /// Copies the referred campaign's steps - template, sequence, delay, message text with its variables, active
    /// flag and media - onto the generated campaign, one step per transaction, skipping steps it already has.
    /// Only the referred campaign's own steps are ever used, never other templates from the tenant's library.
    /// Stops at the first failure, leaving later steps Pending for a retry.
    /// </summary>
    private async Task<bool> AssociateTemplatesAsync(ExecutionRun run, Campaign source, Guid campaignId, CancellationToken cancellationToken)
    {
        var execution = run.Execution;
        var tenantId = execution.TenantId;
        run.Step = LeadDiscoverySteps.Templates;
        await run.Lease.EnsureCanProcessAsync(cancellationToken);

        execution.TemplateStatus = LeadDiscoveryAssociationStatus.Processing;
        await _context.SaveChangesAsync(cancellationToken);

        var sourceSteps = source.Steps.OrderBy(s => s.StepNumber).ToList();
        if (sourceSteps.Count == 0)
        {
            execution.TemplateStatus = LeadDiscoveryAssociationStatus.RetryPending;
            SetError(execution, LeadDiscoverySteps.Templates, "The referred campaign has no templates configured.", null);
            await _context.SaveChangesAsync(cancellationToken);
            return false;
        }

        var templateIds = sourceSteps.Select(s => s.MessageTemplateId).OfType<Guid>().Distinct().ToList();
        var templates = await _context.MessageTemplates.AsNoTracking()
            .Where(t => templateIds.Contains(t.Id) && t.TenantId == tenantId)
            .Select(t => new TemplateInfo(t.Id, t.Name, t.IsActive))
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        var associated = await _context.CampaignSteps.AsNoTracking()
            .Where(s => s.CampaignId == campaignId && s.TenantId == tenantId)
            .Select(s => new { s.StepNumber, s.Id })
            .ToDictionaryAsync(s => s.StepNumber, s => s.Id, cancellationToken);

        var rows = sourceSteps.Select(step => new LeadDiscoveryExecutionTemplate
        {
            TenantId = tenantId,
            ExecutionId = execution.Id,
            CampaignId = campaignId,
            SourceStepId = step.Id,
            TemplateId = step.MessageTemplateId,
            TemplateName = Truncate(TemplateLabel(step, templates), 200)!,
            Sequence = step.StepNumber,
            DelayDaysAfterPrevious = step.DelayDaysAfterPrevious,
            Status = LeadDiscoveryAssociationStatus.Pending
        }).ToList();
        _context.LeadDiscoveryExecutionTemplates.AddRange(rows);
        await _context.SaveChangesAsync(cancellationToken);

        string? firstError = null;
        for (var i = 0; i < sourceSteps.Count && firstError is null; i++)
        {
            var step = sourceSteps[i];
            var row = rows[i];
            await run.Lease.EnsureCanProcessAsync(cancellationToken);

            if (associated.TryGetValue(step.StepNumber, out var existingStepId))
            {
                row.Status = LeadDiscoveryAssociationStatus.Completed;
                row.CampaignStepId = existingStepId;
                row.ErrorMessage = "Already associated - skipped.";
                await _context.SaveChangesAsync(cancellationToken);
                continue;
            }

            var invalid = ValidateTemplate(step, templates);
            if (invalid is not null)
            {
                row.Status = LeadDiscoveryAssociationStatus.Failed;
                row.ErrorMessage = invalid;
                firstError = invalid;
                await _context.SaveChangesAsync(cancellationToken);
                continue;
            }

            firstError = await AssociateTemplateAsync(execution, step, row, campaignId, cancellationToken);
        }

        execution.TemplateStatus = firstError is null ? LeadDiscoveryAssociationStatus.Completed : LeadDiscoveryAssociationStatus.RetryPending;
        if (firstError is not null)
            SetError(execution, LeadDiscoverySteps.Templates, $"Template association failed: {firstError}", null);
        await _context.SaveChangesAsync(cancellationToken);
        return firstError is null;
    }

    private static string TemplateLabel(CampaignStep step, IReadOnlyDictionary<Guid, TemplateInfo> templates) =>
        step.MessageTemplateId is not { } id
            ? $"{step.StepType} (message text only)"
            : templates.TryGetValue(id, out var template) ? template.Name : $"Template {id}";

    /// <summary>A step's template must belong to this tenant and be active. A step with message text only has
    /// nothing to validate here - CampaignService checks sendability when the campaign starts.</summary>
    private static string? ValidateTemplate(CampaignStep step, IReadOnlyDictionary<Guid, TemplateInfo> templates)
    {
        if (step.MessageTemplateId is not { } id)
            return null;
        if (!templates.TryGetValue(id, out var template))
            return "The template does not exist for this tenant.";
        return template.IsActive ? null : $"Template '{template.Name}' is inactive.";
    }

    /// <summary>One template association in its own transaction. Returns the error, or null on success.</summary>
    private async Task<string?> AssociateTemplateAsync(
        LeadDiscoveryExecution execution, CampaignStep step, LeadDiscoveryExecutionTemplate row, Guid campaignId, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        await using (var transaction = await _context.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                var clone = new CampaignStep
                {
                    TenantId = execution.TenantId,
                    CampaignId = campaignId,
                    StepType = step.StepType,
                    StepNumber = step.StepNumber,
                    DelayDaysAfterPrevious = step.DelayDaysAfterPrevious,
                    MessageText = step.MessageText,
                    MessageTemplateId = step.MessageTemplateId,
                    IsActive = step.IsActive
                };
                _context.CampaignSteps.Add(clone);

                var order = 0;
                foreach (var media in step.StepMedia.OrderBy(m => m.DisplayOrder))
                {
                    _context.CampaignStepMedia.Add(new CampaignStepMedia
                    {
                        TenantId = execution.TenantId,
                        CampaignStepId = clone.Id,
                        MediaAssetId = media.MediaAssetId,
                        DisplayOrder = order++
                    });
                }

                row.Status = LeadDiscoveryAssociationStatus.Completed;
                row.CampaignStepId = clone.Id;
                row.ErrorMessage = null;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex;
                await RollbackQuietlyAsync(transaction);
            }
        }

        _logger.LogWarning(failure, "Template association failed for lead discovery execution {ExecutionId}", execution.Id);
        RestoreTracking(execution);
        row.Status = LeadDiscoveryAssociationStatus.Failed;
        row.CampaignStepId = null;
        row.ErrorMessage = Truncate(failure!.Message, ErrorMessageLength);
        _context.LeadDiscoveryExecutionTemplates.Update(row);
        await _context.SaveChangesAsync(cancellationToken);
        return failure.Message;
    }

    /// <summary>Maps each eligible customer that is not yet on the campaign, one transaction each. A failure is
    /// counted and the rest continue; the step is RetryPending if anything failed.</summary>
    private async Task<bool> MapCustomersAsync(ExecutionRun run, Guid campaignId, IReadOnlyList<Guid> eligible, CancellationToken cancellationToken)
    {
        var execution = run.Execution;
        var tenantId = execution.TenantId;
        run.Step = LeadDiscoverySteps.CustomerMapping;
        await run.Lease.EnsureCanProcessAsync(cancellationToken);

        execution.MappingStatus = LeadDiscoveryAssociationStatus.Processing;
        execution.MappingsEligible = eligible.Count;
        await _context.SaveChangesAsync(cancellationToken);

        var alreadyMapped = (await _context.CampaignCustomers.AsNoTracking()
                .Where(cc => cc.CampaignId == campaignId && cc.TenantId == tenantId && eligible.Contains(cc.CustomerId))
                .Select(cc => cc.CustomerId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        int created = 0, existing = 0, failed = 0;
        string? firstError = null;

        foreach (var customerId in eligible)
        {
            if (alreadyMapped.Contains(customerId))
            {
                existing++;
                continue;
            }

            await run.Lease.EnsureCanProcessAsync(cancellationToken);

            Exception? failure = null;
            await using (var transaction = await _context.BeginTransactionAsync(cancellationToken))
            {
                try
                {
                    _context.CampaignCustomers.Add(new CampaignCustomer
                    {
                        TenantId = tenantId,
                        CampaignId = campaignId,
                        CustomerId = customerId,
                        Status = CampaignCustomerStatus.Pending
                    });
                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    created++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex;
                    await RollbackQuietlyAsync(transaction);
                }
            }

            if (failure is null)
                continue;

            RestoreTracking(execution);

            // (CampaignId, CustomerId) is unique: a violation means someone else mapped it first, which is fine.
            var nowMapped = await _context.CampaignCustomers.AsNoTracking()
                .AnyAsync(cc => cc.CampaignId == campaignId && cc.CustomerId == customerId, cancellationToken);
            if (nowMapped)
            {
                existing++;
                continue;
            }

            _logger.LogWarning(failure, "Campaign-customer mapping failed for lead discovery execution {ExecutionId}", execution.Id);
            failed++;
            firstError ??= failure.Message;
        }

        execution.MappingsCreated = created;
        execution.MappingsExisting = existing;
        execution.MappingsFailed = failed;
        execution.MappingStatus = failed == 0 ? LeadDiscoveryAssociationStatus.Completed : LeadDiscoveryAssociationStatus.RetryPending;
        if (firstError is not null)
            SetError(execution, LeadDiscoverySteps.CustomerMapping, $"{failed} campaign-customer mapping(s) failed: {firstError}", null);
        await _context.SaveChangesAsync(cancellationToken);
        return failed == 0;
    }

    /// <summary>Starts the generated campaign once everything is in place - only from Draft, so a campaign a
    /// person has since paused or stopped is left alone. Uses CampaignService's own sendability validation.</summary>
    private async Task ActivateCampaignAsync(ExecutionRun run, Guid campaignId, CancellationToken cancellationToken)
    {
        var execution = run.Execution;
        run.Step = LeadDiscoverySteps.CampaignActivation;
        await run.Lease.EnsureCanProcessAsync(cancellationToken);

        var status = await _context.Campaigns.AsNoTracking()
            .Where(c => c.Id == campaignId && c.TenantId == execution.TenantId)
            .Select(c => (CampaignStatus?)c.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (status != CampaignStatus.Draft)
            return;

        try
        {
            await _campaignService.StartAsync(campaignId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Starting the generated campaign failed for lead discovery execution {ExecutionId}", execution.Id);
            run.ActivationFailed = true;
            RestoreTracking(execution);
            SetError(execution, LeadDiscoverySteps.CampaignActivation, $"The generated campaign could not be started: {ex.Message}", null);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Finalization
    // ------------------------------------------------------------------------------------------------

    private async Task FinalizeAsync(ExecutionRun run, CancellationToken cancellationToken)
    {
        var execution = run.Execution;
        var pending = await ApplyCustomerCountsAsync(execution, cancellationToken);

        var stepUnfinished =
            execution.CampaignStatus is LeadDiscoveryCampaignStatus.RetryPending or LeadDiscoveryCampaignStatus.Failed
                or LeadDiscoveryCampaignStatus.Creating
            || execution.TemplateStatus is LeadDiscoveryAssociationStatus.RetryPending or LeadDiscoveryAssociationStatus.Failed
                or LeadDiscoveryAssociationStatus.Processing
            || execution.MappingStatus is LeadDiscoveryAssociationStatus.RetryPending or LeadDiscoveryAssociationStatus.Failed
                or LeadDiscoveryAssociationStatus.Processing;

        var retryable = execution.CustomersFailed > 0 || pending > 0 || run.Interrupted || run.Fatal || stepUnfinished || run.ActivationFailed
                        || run.VerificationUnfinished;
        // For a retry, customers an earlier attempt of the chain created (Skipped here) count as work done.
        var anySuccess = execution.CustomersCreated > 0 || execution.CustomersSkipped > 0
                         || execution.CampaignStatus == LeadDiscoveryCampaignStatus.Created;

        if (retryable)
        {
            ApplyRetryDecision(execution, anySuccess);
        }
        else if (run.DiscoveryFailed)
        {
            // Only the research itself failed - nothing a retry could resume.
            execution.Status = anySuccess ? LeadDiscoveryExecutionStatus.PartiallyCompleted : LeadDiscoveryExecutionStatus.Failed;
            execution.NextRetryInfo = "Research cannot be resumed. The next scheduled run will discover again.";
        }
        else
        {
            execution.Status = LeadDiscoveryExecutionStatus.Completed;
            execution.NextRetryInfo = null;
        }

        execution.EndedAtUtc = _dateTime.UtcNow;
        execution.QuotaExhausted = run.Stats.QuotaExhausted;
        execution.QuotaStopReason = Truncate(run.Stats.QuotaStopReason, 500);
        execution.Summary = Truncate(BuildSummary(run), 2000);

        // The lock fields belong to the lock store; bring them up to date before writing the whole row, so the
        // final save cannot put back a stale lock state.
        var lockState = await _context.LeadDiscoveryExecutions.AsNoTracking()
            .Where(e => e.Id == execution.Id)
            .Select(e => new
            {
                e.LockStatus, e.LockTokenReference, e.LockOwnerInstanceId, e.LockAcquiredAtUtc, e.LockExpiresAtUtc,
                e.LockLastRenewedAtUtc
            })
            .FirstAsync(cancellationToken);
        execution.LockStatus = lockState.LockStatus;
        execution.LockTokenReference = lockState.LockTokenReference;
        execution.LockOwnerInstanceId = lockState.LockOwnerInstanceId;
        execution.LockAcquiredAtUtc = lockState.LockAcquiredAtUtc;
        execution.LockExpiresAtUtc = lockState.LockExpiresAtUtc;
        execution.LockLastRenewedAtUtc = lockState.LockLastRenewedAtUtc;

        _context.LeadDiscoveryExecutions.Update(execution);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>For an execution with retryable unfinished work: RetryPending while automatic retries remain,
    /// otherwise PartiallyCompleted or Failed with each retryable step marked Failed.</summary>
    private void ApplyRetryDecision(LeadDiscoveryExecution execution, bool anySuccess)
    {
        var max = _options.MaxRetryAttempts;
        if (execution.RetryCount < max)
        {
            execution.Status = LeadDiscoveryExecutionStatus.RetryPending;
            execution.NextRetryInfo =
                $"Automatic retry {execution.RetryCount + 1} of {max} runs with the next scheduled lead discovery run. " +
                "It can also be retried now from Lead Discovery History.";
            return;
        }

        execution.Status = anySuccess ? LeadDiscoveryExecutionStatus.PartiallyCompleted : LeadDiscoveryExecutionStatus.Failed;
        execution.NextRetryInfo = $"Automatic retries exhausted ({max}). It can still be retried manually from Lead Discovery History.";

        if (execution.CampaignStatus == LeadDiscoveryCampaignStatus.RetryPending)
            execution.CampaignStatus = LeadDiscoveryCampaignStatus.Failed;
        if (execution.TemplateStatus == LeadDiscoveryAssociationStatus.RetryPending)
            execution.TemplateStatus = LeadDiscoveryAssociationStatus.Failed;
        if (execution.MappingStatus == LeadDiscoveryAssociationStatus.RetryPending)
            execution.MappingStatus = LeadDiscoveryAssociationStatus.Failed;
    }

    /// <summary>Counts the execution's customer rows onto it. Returns how many are still Pending/Processing.</summary>
    private async Task<int> ApplyCustomerCountsAsync(LeadDiscoveryExecution execution, CancellationToken cancellationToken)
    {
        var executionId = execution.Id;
        var counts = await _context.LeadDiscoveryExecutionCustomers.AsNoTracking()
            .Where(r => r.ExecutionId == executionId)
            .GroupBy(r => new { r.Status, r.IsInvalid })
            .Select(g => new { g.Key.Status, g.Key.IsInvalid, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int Count(LeadDiscoveryCustomerStatus status, bool? invalid = null) =>
            counts.Where(c => c.Status == status && (invalid is null || c.IsInvalid == invalid)).Sum(c => c.Count);

        execution.CustomersDiscovered = counts.Sum(c => c.Count);
        execution.CustomersCreated = Count(LeadDiscoveryCustomerStatus.Created);
        execution.CustomersDuplicate = Count(LeadDiscoveryCustomerStatus.Duplicate);
        execution.CustomersInvalid = Count(LeadDiscoveryCustomerStatus.Skipped, invalid: true);
        execution.CustomersSkipped = Count(LeadDiscoveryCustomerStatus.Skipped, invalid: false);
        execution.CustomersFailed = Count(LeadDiscoveryCustomerStatus.Failed);

        return Count(LeadDiscoveryCustomerStatus.Pending) + Count(LeadDiscoveryCustomerStatus.Processing);
    }

    private static string BuildSummary(ExecutionRun run)
    {
        var e = run.Execution;
        var summary = $"execution={e.Id} status={e.Status} created={e.CustomersCreated} duplicates={e.CustomersDuplicate} " +
                      $"invalid={e.CustomersInvalid} failed={e.CustomersFailed} skipped={e.CustomersSkipped} " +
                      $"campaign={e.CampaignStatus} templates={e.TemplateStatus} mappings={e.MappingStatus}";

        if (e.RetryOfExecutionId is { } retried)
            summary += $" retryOf={retried} attempt={e.RetryCount}";
        if (e.FailedStep is not null)
            summary += $" failedStep={e.FailedStep}";
        if (e.RetryOfExecutionId is null)
            summary += " " + run.Stats.ToSummary();
        if (run.NotConfiguredReason is not null)
            summary = $"Skipped: {run.NotConfiguredReason} {summary}";

        return summary;
    }

    /// <summary>
    /// Writes the tenant-visible record of what this run's research cost (see <see cref="LeadDiscoveryRun"/>),
    /// priced at the configured rates for the model that ran it. Only for a run that actually researched - a
    /// simulated run does get a row, with zeros. Never allowed to fail the execution.
    /// </summary>
    private async Task RecordRunCostAsync(ExecutionRun run)
    {
        var stats = run.Stats;
        if (stats.Rounds == 0 || run.NotConfiguredReason is not null || string.IsNullOrEmpty(stats.Model))
            return;

        try
        {
            _context.LeadDiscoveryRuns.Add(new LeadDiscoveryRun
            {
                TenantId = run.Profile.TenantId,
                ExecutionId = run.Execution.Id,
                RanAtUtc = _dateTime.UtcNow,
                Model = Truncate(stats.Model, LeadDiscoveryLimits.Model) ?? string.Empty,
                Rounds = stats.Rounds,
                CandidatesConsidered = stats.Candidates,
                LeadsSaved = stats.Saved,
                Duplicates = stats.Duplicates,
                Rejected = stats.Rejected,
                InputTokens = stats.Usage.InputTokens,
                OutputTokens = stats.Usage.OutputTokens,
                CacheReadTokens = stats.Usage.CacheReadInputTokens,
                CacheWriteTokens = stats.Usage.CacheCreationInputTokens,
                WebSearches = stats.Usage.WebSearches,
                WebFetches = stats.Usage.WebFetches,
                EstimatedCostUsd = LeadDiscoveryCost.Estimate(stats.Usage, stats.Model, _pricing),
                SearchCostUsd = LeadDiscoveryCost.SearchCost(stats.Usage, _pricing, stats.Model),
                ResearchNote = Truncate(stats.NotesText(), 2000)
            });

            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the lead discovery run cost for tenant {TenantId}", run.Profile.TenantId);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    /// <summary>After a rollback the change tracker may hold half-saved entities (including audit rows the audit
    /// interceptor added); drop all of it and re-attach the execution as it was last saved.</summary>
    private void RestoreTracking(LeadDiscoveryExecution execution)
    {
        _context.ResetChangeTracker();
        _context.LeadDiscoveryExecutions.Attach(execution);
    }

    private static async Task RollbackQuietlyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // The connection may already be gone; disposing the transaction rolls it back regardless.
        }
    }

    private static void SetError(LeadDiscoveryExecution execution, string step, string message, string? details)
    {
        execution.FailedStep ??= step;
        execution.ErrorMessage ??= Truncate(message, ErrorMessageLength);
        execution.ErrorDetails ??= Truncate(details, ErrorDetailsLength);
    }

    /// <summary>"&lt;Referred Campaign Name&gt; - YYYY-MM-DD", the referred name shortened if the whole would not
    /// fit Campaign.Name.</summary>
    public static string BuildCampaignName(string referredName, DateTime processingDate)
    {
        var suffix = $" - {processingDate:yyyy-MM-dd}";
        return Truncate(referredName, CampaignNameMaxLength - suffix.Length) + suffix;
    }

    private static LeadDiscoveryExecutionCustomer NewRow(
        LeadDiscoveryExecution execution, string? name, string? phone, LeadDiscoveryCustomerStatus status, string? message, bool isInvalid = false) => new()
    {
        TenantId = execution.TenantId,
        ExecutionId = execution.Id,
        CustomerName = Truncate(string.IsNullOrWhiteSpace(name) ? "(unnamed business)" : name, 300)!,
        Phone = Truncate(phone, LeadDiscoveryLimits.Phone),
        Status = status,
        IsInvalid = isInvalid,
        ErrorMessage = Truncate(message, ErrorMessageLength)
    };

    private static LeadDiscoveryExecutionCustomer NewPendingRow(LeadDiscoveryExecution execution, VerifiedLead lead)
    {
        var row = NewRow(execution, lead.BusinessName, lead.Phone, LeadDiscoveryCustomerStatus.Pending, null);
        row.CandidateJson = JsonSerializer.Serialize(lead);
        return row;
    }

    private static VerifiedLead? DeserializeCandidate(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<VerifiedLead>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The leads that match neither an existing discovered lead, a customer (including soft-deleted
    /// ones, which were removed on purpose), nor an earlier lead in the same list. Order is preserved.</summary>
    private async Task<List<VerifiedLead>> RemoveDuplicatesAsync(Guid tenantId, IReadOnlyList<VerifiedLead> leads, CancellationToken cancellationToken)
    {
        if (leads.Count == 0)
            return new List<VerifiedLead>();

        var phoneKeys = leads.Select(l => l.PhoneKey).OfType<string>().Distinct().ToList();
        var websiteKeys = leads.Select(l => l.WebsiteKey).OfType<string>().Distinct().ToList();
        var nameKeys = leads.Select(l => l.NameKey).Distinct().ToList();
        var phoneNumbers = leads.Select(l => l.PhoneE164).OfType<string>().Distinct().ToList();

        var existing = await _context.DiscoveredLeads
            .Where(l => l.TenantId == tenantId
                        && ((l.PhoneKey != null && phoneKeys.Contains(l.PhoneKey))
                            || (l.WebsiteKey != null && websiteKeys.Contains(l.WebsiteKey))
                            || nameKeys.Contains(l.NameKey)))
            .Select(l => new { l.PhoneKey, l.WebsiteKey, l.NameKey })
            .ToListAsync(cancellationToken);

        // Soft-deleted customers do NOT count as taken (default query filter excludes them) - their
        // phone number is free for reuse (see CustomerConfiguration's filtered unique index), so a
        // rediscovered lead whose only "match" is a deleted CRM row should get a fresh Customer, not
        // be treated as already known.
        var customerPhones = await _context.Customers
            .Where(c => c.TenantId == tenantId && phoneNumbers.Contains(c.PhoneNumberE164))
            .Select(c => c.PhoneNumberE164)
            .ToListAsync(cancellationToken);

        var takenPhoneKeys = existing.Select(e => e.PhoneKey).OfType<string>().ToHashSet();
        var takenWebsiteKeys = existing.Select(e => e.WebsiteKey).OfType<string>().ToHashSet();
        var takenNameKeys = existing.Select(e => e.NameKey).ToHashSet();
        var takenPhoneNumbers = customerPhones.ToHashSet();

        var fresh = new List<VerifiedLead>();
        foreach (var lead in leads)
        {
            var isDuplicate = (lead.PhoneKey is { } phoneKey && takenPhoneKeys.Contains(phoneKey))
                              || (lead.PhoneE164 is { } e164 && takenPhoneNumbers.Contains(e164))
                              || (lead.WebsiteKey is { } websiteKey && takenWebsiteKeys.Contains(websiteKey))
                              || takenNameKeys.Contains(lead.NameKey);

            if (isDuplicate)
                continue;

            fresh.Add(lead);
            if (lead.PhoneKey is not null) takenPhoneKeys.Add(lead.PhoneKey);
            if (lead.WebsiteKey is not null) takenWebsiteKeys.Add(lead.WebsiteKey);
            takenNameKeys.Add(lead.NameKey);
        }

        return fresh;
    }

    private static DiscoveredLead ToEntity(Guid tenantId, VerifiedLead lead) => new()
    {
        TenantId = tenantId,
        BusinessName = Truncate(lead.BusinessName, LeadDiscoveryLimits.BusinessName)!,
        BusinessType = Truncate(lead.BusinessType, LeadDiscoveryLimits.BusinessType)!,
        ContactPerson = Truncate(lead.ContactPerson, LeadDiscoveryLimits.ContactPerson),
        Address = Truncate(lead.Address, LeadDiscoveryLimits.Address),
        City = Truncate(lead.City, LeadDiscoveryLimits.City),
        State = Truncate(lead.State, LeadDiscoveryLimits.State),
        Phone = Truncate(lead.Phone, LeadDiscoveryLimits.Phone),
        PhoneE164 = lead.PhoneE164,
        PhoneVerified = lead.Phone is not null,
        PhoneSourceUrl = Truncate(lead.PhoneSourceUrl, LeadDiscoveryLimits.Url),
        Email = Truncate(lead.Email, LeadDiscoveryLimits.Email),
        Website = Truncate(lead.Website, LeadDiscoveryLimits.Url),
        SourceUrl = Truncate(lead.SourceUrl, LeadDiscoveryLimits.Url)!,
        LeadScore = lead.LeadScore,
        ScoreRationale = Truncate(lead.ScoreRationale, LeadDiscoveryLimits.ScoreRationale),
        PhoneKey = lead.PhoneKey,
        WebsiteKey = Truncate(lead.WebsiteKey, LeadDiscoveryLimits.DedupeKey),
        NameKey = Truncate(lead.NameKey, LeadDiscoveryLimits.DedupeKey)!
    };

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;

    private sealed record TemplateInfo(Guid Id, string Name, bool IsActive);

    private enum CustomerOutcome
    {
        Created,
        LeadOnly,
        Duplicate,
        Failed
    }

    /// <summary>In-memory state of one execution while it runs.</summary>
    private sealed class ExecutionRun
    {
        public ExecutionRun(LeadDiscoveryExecution execution, LeadDiscoveryProfile profile, LeadDiscoveryLease lease, int batchSize)
        {
            Execution = execution;
            Profile = profile;
            Lease = lease;
            BatchSize = batchSize;
            Stats = new RunStats(batchSize);
        }

        public LeadDiscoveryExecution Execution { get; }
        public LeadDiscoveryProfile Profile { get; }
        public LeadDiscoveryLease Lease { get; }
        public int BatchSize { get; }
        public RunStats Stats { get; }

        public string Step { get; set; } = LeadDiscoverySteps.Customers;
        public bool Interrupted { get; set; }
        public bool Fatal { get; set; }
        public bool DiscoveryFailed { get; set; }
        public bool ActivationFailed { get; set; }

        /// <summary>A number could not be checked (Error) or has no answer yet (Pending): the execution stays retryable so a
        /// retry asks again, and its customer is kept out of the campaign until then.</summary>
        public bool VerificationUnfinished { get; set; }
        public string? NotConfiguredReason { get; set; }

        /// <summary>A retry of an execution that was charged per candidate when it was researched: its customers are already paid for.</summary>
        public bool LeadsAlreadyBilled { get; set; }
    }

    private sealed class RunStats
    {
        private readonly int _batchSize;
        private readonly Dictionary<string, int> _rejections = new();

        public RunStats(int batchSize) => _batchSize = batchSize;

        public string Model { get; set; } = string.Empty;
        public int Rounds { get; set; }
        public int Candidates { get; set; }
        public int Saved { get; set; }

        /// <summary>Customers created this run - the figure the batch is measured against, and the leads the tenant is charged for.</summary>
        public int Created { get; set; }

        /// <summary>Customers actually charged for (fewer than <see cref="Created"/> only when the wallet ran dry mid-run).</summary>
        public int Billed { get; set; }
        public int Duplicates { get; set; }
        public bool QuotaExhausted { get; set; }

        /// <summary>Why the research stopped for want of allowance, when it did (see LeadDiscoveryExecution.QuotaStopReason).</summary>
        public string? QuotaStopReason { get; set; }

        /// <summary>Stopped because a monthly limit or the plan ran out, not the wallet.</summary>
        public bool StoppedEarlyForAllowance { get; set; }
        public LeadDiscoveryUsage Usage { get; set; } = LeadDiscoveryUsage.None;

        public int Rejected => _rejections.Values.Sum();

        private readonly List<string> _notes = new();

        /// <summary>The agent's own explanation for a round, kept so the run can say why it found what it found.</summary>
        public void AddNote(string? note)
        {
            if (!string.IsNullOrWhiteSpace(note))
                _notes.Add(note.Trim());
        }

        /// <summary>One line per round - "Round 2: ..." - or just the text when there was one round. Null when the agent gave none.</summary>
        public string? NotesText() => _notes.Count switch
        {
            0 => null,
            1 => _notes[0],
            _ => string.Join("\n", _notes.Select((n, i) => $"Round {i + 1}: {n}"))
        };

        public void Reject(string reason) => _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;

        /// <summary>What the research consumed - enough to price a run from the job list without opening the
        /// Anthropic Console. A simulated run reports zeros for the token counts.</summary>
        public string ToSummary()
        {
            var summary = $"saved={Saved} created={Created} charged={Billed} batchSize={_batchSize} rounds={Rounds} candidates={Candidates} rejectedNotCharged={Rejected} " +
                          $"inputTokens={Usage.InputTokens} outputTokens={Usage.OutputTokens} " +
                          $"cacheReadTokens={Usage.CacheReadInputTokens} cacheWriteTokens={Usage.CacheCreationInputTokens} " +
                          $"webSearches={Usage.WebSearches} webFetches={Usage.WebFetches}";

            if (QuotaExhausted)
                summary += " stoppedEarly=quotaExhausted";
            else if (StoppedEarlyForAllowance)
                summary += " stoppedEarly=allowanceLimit";

            return _rejections.Count == 0
                ? summary
                : $"{summary} rejections: {string.Join("; ", _rejections.Select(r => $"{r.Key}={r.Value}"))}";
        }
    }
}

/// <summary>What started an execution.</summary>
public static class LeadDiscoveryTriggers
{
    public const string Scheduled = "Scheduled";
    public const string ManualRun = "Manual run";
    public const string AutomaticRetry = "Automatic retry";
    public const string ManualRetry = "Manual retry";
}

/// <summary>Names for LeadDiscoveryExecution.FailedStep.</summary>
public static class LeadDiscoverySteps
{
    public const string LockAcquisition = "LockAcquisition";
    public const string Discovery = "Discovery";
    public const string Customers = "Customers";
    public const string Verification = "Verification";
    public const string ConsentSms = "ConsentSms";
    public const string Campaign = "Campaign";
    public const string Templates = "Templates";
    public const string CustomerMapping = "CustomerMapping";
    public const string CampaignActivation = "CampaignActivation";
    public const string Interrupted = "Interrupted";
}

/// <summary>Which executions a retry may resume.</summary>
public static class LeadDiscoveryRetryRules
{
    /// <summary>The newest attempt of its chain, finished with unfinished work, and not a blocked attempt (which
    /// did nothing - its predecessor is the one to retry).</summary>
    public static bool CanRetry(LeadDiscoveryExecution execution) =>
        execution.SupersededByExecutionId is null
        && execution.LockStatus != LeadDiscoveryLockStatus.Blocked
        && execution.Status is LeadDiscoveryExecutionStatus.RetryPending
            or LeadDiscoveryExecutionStatus.PartiallyCompleted
            or LeadDiscoveryExecutionStatus.Failed;
}
