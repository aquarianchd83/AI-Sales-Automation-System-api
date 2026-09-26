using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
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
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantTimeZoneProvider _tenantTimeZone;
    private readonly ICampaignService _campaignService;
    private readonly ILeadDiscoveryLockStore _lockStore;
    private readonly IApplicationInstance _instance;
    private readonly ILogger<LeadDiscoveryRunService> _logger;
    private readonly LeadDiscoveryOptions _options;
    private readonly LeadDiscoveryPricingOptions _pricing;

    public LeadDiscoveryRunService(
        IApplicationDbContext context,
        ILeadDiscoveryAgent agent,
        IPlanLimitsService planLimits,
        IQuotaGate quota,
        IDateTimeProvider dateTime,
        ITenantTimeZoneProvider tenantTimeZone,
        ICampaignService campaignService,
        ILeadDiscoveryLockStore lockStore,
        IApplicationInstance instance,
        ILogger<LeadDiscoveryRunService> logger,
        IOptions<LeadDiscoveryOptions> options,
        IOptionsSnapshot<LeadDiscoveryPricingOptions> pricing)
    {
        _context = context;
        _agent = agent;
        _planLimits = planLimits;
        _quota = quota;
        _dateTime = dateTime;
        _tenantTimeZone = tenantTimeZone;
        _campaignService = campaignService;
        _lockStore = lockStore;
        _instance = instance;
        _logger = logger;
        _options = options.Value;
        _pricing = pricing.Value;
    }

    public async Task<string> RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var profile = await LoadProfileAsync(tenantId, null, cancellationToken);

        if (profile is null)
            return "Skipped: no lead discovery profile has been set up.";
        if (!profile.IsEnabled)
            return "Skipped: lead discovery is turned off in the profile.";

        var summaries = new List<string>();

        // Unfinished work first, oldest first - each resumed by its own new execution.
        foreach (var retryId in await FindAutomaticRetryTargetsAsync(profile, cancellationToken))
        {
            var previous = await _context.LeadDiscoveryExecutions
                .FirstOrDefaultAsync(e => e.Id == retryId && e.TenantId == tenantId, cancellationToken);
            if (previous is not null && LeadDiscoveryRetryRules.CanRetry(previous))
                summaries.Add("retry: " + await ExecuteAsync(profile, previous, LeadDiscoveryTriggers.AutomaticRetry, 0, cancellationToken));
        }

        summaries.Add(await RunFreshAsync(profile, cancellationToken));
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

        return await ExecuteAsync(profile, previous, LeadDiscoveryTriggers.ManualRetry, 0, cancellationToken);
    }

    private Task<LeadDiscoveryProfile?> LoadProfileAsync(Guid tenantId, Guid? profileId, CancellationToken cancellationToken) =>
        _context.LeadDiscoveryProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && (profileId == null || p.Id == profileId))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string> RunFreshAsync(LeadDiscoveryProfile profile, CancellationToken cancellationToken)
    {
        if (profile.Keywords.Count == 0 || profile.Locations.Count == 0)
            return "Skipped: the profile needs at least one keyword and one location.";

        var planLimit = await _planLimits.GetLeadDiscoveryBatchLimitAsync(profile.TenantId, cancellationToken);
        var batchSize = Math.Min(profile.BatchSize, planLimit ?? int.MaxValue);
        if (batchSize <= 0)
            return "Skipped: the tenant's plan allows no discovered leads per run.";

        // Prepaid: every candidate the agent evaluates is charged - fresh, duplicate or rejected - because the
        // provider bills the research either way. No candidates left means no run, and no agent call.
        if (await _quota.GetAvailableAsync(profile.TenantId, QuotaType.LeadCandidates, cancellationToken) < 1)
            return "Skipped: no lead-candidate quota left - buy credits or wait for the plan to renew.";

        return await ExecuteAsync(profile, null, LeadDiscoveryTriggers.Scheduled, batchSize, cancellationToken);
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
        LeadDiscoveryProfile profile, LeadDiscoveryExecution? previous, string trigger, int batchSize, CancellationToken cancellationToken)
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
        var runKey = run.Execution.Id.ToString("N");

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
            while (stats.Saved < run.BatchSize && stats.Rounds < _options.MaxRounds)
            {
                run.Step = LeadDiscoverySteps.Discovery;
                await run.Lease.EnsureCanProcessAsync(cancellationToken);

                // Never ask the agent for more candidates than the tenant can still pay for.
                var affordable = await _quota.GetAvailableAsync(tenantId, QuotaType.LeadCandidates, cancellationToken);
                if (affordable < 1)
                {
                    stats.QuotaExhausted = true;
                    break;
                }

                stats.Rounds++;
                var remaining = run.BatchSize - stats.Saved;

                LeadDiscoveryAgentResult result;
                try
                {
                    result = await _agent.DiscoverAsync(new LeadDiscoveryAgentRequest(
                        profile.TargetBusinessType,
                        profile.Keywords,
                        profile.Locations,
                        (int)Math.Min(Math.Min(remaining, _options.MaxCandidatesPerRound), Math.Floor(affordable)),
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

                if (result.Candidates.Count == 0)
                    break;

                var assessments = LeadQualification.AssessAll(result.Candidates, result.Evidence, rules);
                var rows = new List<LeadDiscoveryExecutionCustomer>();
                var qualified = new List<VerifiedLead>();
                for (var i = 0; i < assessments.Count; i++)
                {
                    if (assessments[i].Lead is { } lead)
                    {
                        qualified.Add(lead);
                        continue;
                    }

                    stats.Reject(assessments[i].RejectionReason!);
                    var candidate = result.Candidates[i];
                    rows.Add(NewRow(run.Execution, candidate.BusinessName, candidate.Phone, LeadDiscoveryCustomerStatus.Skipped,
                        $"Invalid: {assessments[i].RejectionReason}", isInvalid: true));
                }

                var fresh = await RemoveDuplicatesAsync(tenantId, qualified, cancellationToken);
                var freshSet = new HashSet<VerifiedLead>(fresh, ReferenceEqualityComparer.Instance);
                foreach (var duplicate in qualified.Where(l => !freshSet.Contains(l)))
                {
                    stats.Duplicates++;
                    rows.Add(NewRow(run.Execution, duplicate.BusinessName, duplicate.Phone, LeadDiscoveryCustomerStatus.Duplicate,
                        "Matches an existing customer or discovered lead."));
                }

                var toSave = fresh.Take(remaining).ToList();

                // Charged for what was evaluated this round, with the split recorded on the ledger entry so the
                // tenant can see why 25 candidates cost 25 units when only 12 became leads.
                await _quota.ConsumeLeadCandidatesAsync(
                    tenantId,
                    result.Candidates.Count,
                    $"lead:{runKey}:r{stats.Rounds}",
                    runKey,
                    $"{result.Candidates.Count} candidates: {toSave.Count} new, {qualified.Count - fresh.Count} duplicate, {assessments.Count - qualified.Count} rejected",
                    cancellationToken);

                // The quota ledger resets the change tracker when it retries a concurrency conflict.
                _context.LeadDiscoveryExecutions.Attach(run.Execution);

                // Recorded Pending, with the candidate, before any of them is processed: an execution that stops
                // part-way leaves exactly the unprocessed ones for a retry.
                var pending = toSave.Select(lead => (Lead: lead, Row: NewPendingRow(run.Execution, lead))).ToList();
                rows.AddRange(pending.Select(p => p.Row));
                _context.LeadDiscoveryExecutionCustomers.AddRange(rows);
                await _context.SaveChangesAsync(cancellationToken);

                run.Step = LeadDiscoverySteps.Customers;
                foreach (var (lead, row) in pending)
                {
                    await run.Lease.EnsureCanProcessAsync(cancellationToken);
                    var outcome = await ProcessCustomerAsync(run, row, lead, cancellationToken);

                    if (outcome is CustomerOutcome.Created or CustomerOutcome.LeadOnly)
                    {
                        stats.Saved++;
                        knownBusinesses.Add(lead.City is null ? lead.BusinessName : $"{lead.BusinessName}, {lead.City}");
                    }
                }

                if (toSave.Count == 0)
                    break;
            }
        }
        finally
        {
            await RecordRunCostAsync(run);
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
                rows.Add(skipped);
                continue;
            }

            var unfinished = earlier.Status is LeadDiscoveryCustomerStatus.Pending or LeadDiscoveryCustomerStatus.Processing
                or LeadDiscoveryCustomerStatus.Failed;
            if (!unfinished || DeserializeCandidate(earlier.CandidateJson) is not { } lead)
                continue;

            var row = NewPendingRow(run.Execution, lead);
            row.RetriedFromId = earlier.Id;
            rows.Add(row);
            toProcess.Add((lead, row));
        }

        _context.LeadDiscoveryExecutionCustomers.AddRange(rows);
        await _context.SaveChangesAsync(cancellationToken);

        foreach (var (lead, row) in toProcess)
        {
            await run.Lease.EnsureCanProcessAsync(cancellationToken);
            await ProcessCustomerAsync(run, row, lead, cancellationToken);
        }
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

                var customer = new Customer
                {
                    TenantId = tenantId,
                    PhoneNumberE164 = phoneE164,
                    // Customer is person-shaped, a discovered lead is a business: the named contact when the
                    // research found one, the business name otherwise, so the CRM row is recognisable either way.
                    FirstName = Truncate(lead.ContactPerson ?? lead.BusinessName, CustomerNameLength),
                    Email = Truncate(lead.Email, LeadDiscoveryLimits.Email),
                    Source = CustomerSource,
                    // WhatsApp message consent and status for a newly discovered customer, with the consent
                    // evidence fields saying where it came from.
                    OptInStatus = OptInStatus.OptedIn,
                    OptInTimestamp = now,
                    OptInSource = CustomerSource
                };
                _context.Customers.Add(customer);
                discovered.CustomerId = customer.Id;

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
            return;
        }

        var eligible = await EligibleCustomerIdsAsync(execution, cancellationToken);
        execution.MappingsEligible = eligible.Count;

        if (eligible.Count == 0)
        {
            SkipCampaign(execution, "No new customers discovered/created. Campaign creation skipped.");
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
    private async Task<List<Guid>> EligibleCustomerIdsAsync(LeadDiscoveryExecution execution, CancellationToken cancellationToken)
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

        return created.Where(existing.Contains).Distinct().ToList();
    }

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

        var existing = await FindGeneratedCampaignAsync(execution, source.Id, cancellationToken);
        if (existing is not null)
            return await ReuseCampaignAsync(execution, existing, cancellationToken);

        var campaign = new Campaign
        {
            TenantId = execution.TenantId,
            Name = BuildCampaignName(source.Name, execution.ProcessingDate),
            Description = source.Description,
            Status = CampaignStatus.Draft,
            // The referred campaign's sending time, on the processing date.
            ScheduledStartAt = source.ScheduledStartAt is { } sourceSchedule
                ? execution.ProcessingDate.Date + sourceSchedule.TimeOfDay
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
            execution.CampaignNote = "Campaign created.";
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

        var retryable = execution.CustomersFailed > 0 || pending > 0 || run.Interrupted || run.Fatal || stepUnfinished || run.ActivationFailed;
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
                EstimatedCostUsd = LeadDiscoveryCost.Estimate(stats.Usage, stats.Model, _pricing)
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

        // IgnoreQueryFilters so soft-deleted customers still count; the tenant is re-applied by hand.
        var customerPhones = await _context.Customers.IgnoreQueryFilters()
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
        public string? NotConfiguredReason { get; set; }
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
        public int Duplicates { get; set; }
        public bool QuotaExhausted { get; set; }
        public LeadDiscoveryUsage Usage { get; set; } = LeadDiscoveryUsage.None;

        public int Rejected => _rejections.Values.Sum();

        public void Reject(string reason) => _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;

        /// <summary>What the research consumed - enough to price a run from the job list without opening the
        /// Anthropic Console. A simulated run reports zeros for the token counts.</summary>
        public string ToSummary()
        {
            var summary = $"saved={Saved} batchSize={_batchSize} rounds={Rounds} candidates={Candidates} " +
                          $"inputTokens={Usage.InputTokens} outputTokens={Usage.OutputTokens} " +
                          $"cacheReadTokens={Usage.CacheReadInputTokens} cacheWriteTokens={Usage.CacheCreationInputTokens} " +
                          $"webSearches={Usage.WebSearches} webFetches={Usage.WebFetches}";

            if (QuotaExhausted)
                summary += " stoppedEarly=quotaExhausted";

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
    public const string AutomaticRetry = "Automatic retry";
    public const string ManualRetry = "Manual retry";
}

/// <summary>Names for LeadDiscoveryExecution.FailedStep.</summary>
public static class LeadDiscoverySteps
{
    public const string LockAcquisition = "LockAcquisition";
    public const string Discovery = "Discovery";
    public const string Customers = "Customers";
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
