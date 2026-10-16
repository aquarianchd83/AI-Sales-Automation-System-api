using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>Queues a manual lead discovery retry as a background job.</summary>
public interface ILeadDiscoveryRetryScheduler
{
    /// <summary>Returns the background job id.</summary>
    string EnqueueRetry(Guid tenantId, Guid executionId);
}

/// <summary>Lead Discovery History for the calling tenant, and manual retry.</summary>
public interface ILeadDiscoveryHistoryService
{
    /// <summary>Executions grouped by processing date, newest date first.</summary>
    Task<PagedResult<LeadDiscoveryHistoryDayDto>> GetHistoryAsync(LeadDiscoveryHistoryQuery query, CancellationToken cancellationToken = default);

    Task<LeadDiscoveryExecutionDetailDto> GetExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>Queues a retry of a retryable execution. Throws ConflictException when it is not retryable.</summary>
    Task<LeadDiscoveryRetryQueuedDto> RequestRetryAsync(Guid executionId, CancellationToken cancellationToken = default);
}

public class LeadDiscoveryHistoryService : ILeadDiscoveryHistoryService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly ILeadDiscoveryRetryScheduler _retryScheduler;

    public LeadDiscoveryHistoryService(IApplicationDbContext context, ITenantContext tenantContext, ILeadDiscoveryRetryScheduler retryScheduler)
    {
        _context = context;
        _tenantContext = tenantContext;
        _retryScheduler = retryScheduler;
    }

    public async Task<PagedResult<LeadDiscoveryHistoryDayDto>> GetHistoryAsync(LeadDiscoveryHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId();
        var executions = _context.LeadDiscoveryExecutions.AsNoTracking().Where(e => e.TenantId == tenantId);

        if (query.From is { } from)
            executions = executions.Where(e => e.ProcessingDate >= from.Date);
        if (query.To is { } to)
            executions = executions.Where(e => e.ProcessingDate <= to.Date);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<LeadDiscoveryExecutionStatus>(query.Status, ignoreCase: true, out var status))
                throw new FluentValidation.ValidationException($"Unknown execution status '{query.Status}'.");
            executions = executions.Where(e => e.Status == status);
        }

        var dates = executions.Select(e => e.ProcessingDate).Distinct();
        var totalDays = await dates.CountAsync(cancellationToken);
        var pageDates = await dates
            .OrderByDescending(d => d)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var rows = await executions
            .Where(e => pageDates.Contains(e.ProcessingDate))
            .OrderByDescending(e => e.StartedAtUtc)
            .ToListAsync(cancellationToken);

        var executionIds = rows.Select(e => e.Id).ToList();
        var runByExecutionId = await _context.LeadDiscoveryRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ExecutionId != null && executionIds.Contains(r.ExecutionId!.Value))
            .ToDictionaryAsync(r => r.ExecutionId!.Value, cancellationToken);
        var pricing = await ResolvePricingAsync(tenantId, cancellationToken);

        var days = pageDates
            .Select(date => new LeadDiscoveryHistoryDayDto(
                date,
                rows.Where(e => e.ProcessingDate == date)
                    .Select(e => ToSummary(e, runByExecutionId.GetValueOrDefault(e.Id), pricing))
                    .ToList()))
            .ToList();

        return new PagedResult<LeadDiscoveryHistoryDayDto>(days, totalDays, query.Page, query.PageSize);
    }

    /// <summary>The note shown when a row was not sent the consent SMS - never blank - and whether it is because the switch is Off (so the screen links to the setting).</summary>
    internal static (string Note, bool NeedsSetting) ConsentSmsNoteFor(LeadDiscoveryExecutionCustomerDto row, IReadOnlyDictionary<Guid, string?> skippedReasons)
    {
        const string off = "Not sent: the consent SMS was Off";

        if (row.CustomerId is not { } customerId)
        {
            return row.VerificationStatus switch
            {
                // Rejected by the DND check: no customer, so the consent SMS logic never runs for it.
                nameof(LeadDiscoveryVerificationStatus.Fail) => ("Not sent: Not applicable", false),
                nameof(LeadDiscoveryVerificationStatus.Pending) or nameof(LeadDiscoveryVerificationStatus.Error) =>
                    ("Not sent: the DND check has not completed yet, so no customer exists to invite.", false),
                _ => ("Not sent: Not applicable", false)
            };
        }

        // No invitation exists for this customer. Unless a specific reason was recorded (no WhatsApp number, say), the consent SMS was Off.
        if (skippedReasons.TryGetValue(customerId, out var reason)
            && !string.IsNullOrWhiteSpace(reason)
            && reason != LeadDiscoveryRunService.ConsentSmsOffReason)
            return ($"Not sent: {reason}", false);

        return (off, true);
    }

    public async Task<LeadDiscoveryExecutionDetailDto> GetExecutionAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId();
        var execution = await _context.LeadDiscoveryExecutions.AsNoTracking()
                            .FirstOrDefaultAsync(e => e.Id == executionId && e.TenantId == tenantId, cancellationToken)
                        ?? throw new NotFoundException($"Lead discovery execution {executionId} was not found.");

        var customers = await _context.LeadDiscoveryExecutionCustomers.AsNoTracking()
            .Where(c => c.ExecutionId == executionId && c.TenantId == tenantId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new LeadDiscoveryExecutionCustomerDto(
                c.Id, c.CustomerId, c.DiscoveredLeadId, c.CustomerName, c.Phone, c.Status.ToString(), c.IsInvalid,
                c.ErrorMessage, c.ErrorDetails, c.RetriedFromId, c.ProcessedAtUtc,
                c.VerificationStatus.ToString(), c.VerificationReasonCode, c.VerificationReason, c.VerifiedAtUtc, null, null, null, null, null, null, false))
            .ToListAsync(cancellationToken);

        var customerIds = customers.Where(c => c.CustomerId != null).Select(c => c.CustomerId!.Value).Distinct().ToList();
        if (customerIds.Count > 0)
        {
            var names = (await _context.Customers.AsNoTracking()
                    .Where(c => c.TenantId == tenantId && customerIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.FirstName, c.LastName })
                    .ToListAsync(cancellationToken))
                .ToDictionary(c => c.Id, c => string.Join(' ', new[] { c.FirstName, c.LastName }.Where(n => !string.IsNullOrWhiteSpace(n))));

            customers = customers
                .Select(c => c.CustomerId is { } id && names.TryGetValue(id, out var name) && name.Length > 0 ? c with { CrmCustomerName = name } : c)
                .ToList();
        }

        // Which rows' customers ended up in the campaign this execution generated.
        if (execution.GeneratedCampaignId is { } generatedId && customerIds.Count > 0)
        {
            var inCampaign = (await _context.CampaignCustomers.AsNoTracking()
                    .Where(cc => cc.CampaignId == generatedId && customerIds.Contains(cc.CustomerId))
                    .Select(cc => cc.CustomerId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            customers = customers
                .Select(c => c.CustomerId is { } id && inCampaign.Contains(id) ? c with { CampaignName = execution.GeneratedCampaignName } : c)
                .ToList();
        }

        // The consent SMS each customer was sent in this execution, and whether they have answered it.
        if (customerIds.Count > 0)
        {
            var invitations = (await _context.ConsentInvitations.AsNoTracking()
                    .Where(i => i.ExecutionId == executionId && customerIds.Contains(i.CustomerId))
                    .Select(i => new { i.CustomerId, i.Status, i.FailureReason, i.ConsentRecordedAtUtc })
                    .ToListAsync(cancellationToken))
                .GroupBy(i => i.CustomerId)
                .ToDictionary(g => g.Key, g => g.First());

            customers = customers
                .Select(c => c.CustomerId is { } id && invitations.TryGetValue(id, out var inv)
                    ? c with { ConsentSmsStatus = inv.Status.ToString(), ConsentSmsFailureReason = inv.FailureReason, ConsentRecordedAtUtc = inv.ConsentRecordedAtUtc }
                    : c)
                .ToList();
        }

        // Rows that got no SMS say why. The reason recorded when the invitation was skipped wins; otherwise it follows from the row.
        var skippedReasons = customerIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : (await _context.CustomerHistoryEvents.AsNoTracking()
                    .Where(e => e.ExecutionId == executionId && e.EventType == CustomerHistoryEventTypes.SmsSkipped && customerIds.Contains(e.CustomerId))
                    .OrderBy(e => e.OccurredAtUtc)
                    .Select(e => new { e.CustomerId, e.Reason })
                    .ToListAsync(cancellationToken))
                .GroupBy(e => e.CustomerId)
                .ToDictionary(g => g.Key, g => g.Last().Reason);

        customers = customers.Select(c =>
        {
            if (c.ConsentSmsStatus is not null)
                return c;

            var (note, needsSetting) = ConsentSmsNoteFor(c, skippedReasons);
            return c with { ConsentSmsNote = note, ConsentSmsNeedsSetting = needsSetting };
        }).ToList();

        var templates = await _context.LeadDiscoveryExecutionTemplates.AsNoTracking()
            .Where(t => t.ExecutionId == executionId && t.TenantId == tenantId)
            .OrderBy(t => t.Sequence)
            .Select(t => new LeadDiscoveryExecutionTemplateDto(
                t.Id, t.TemplateId, t.TemplateName, t.Sequence, t.DelayDaysAfterPrevious, t.CampaignStepId, t.Status.ToString(), t.ErrorMessage))
            .ToListAsync(cancellationToken);

        var transitions = await _context.LeadDiscoveryLockTransitions.AsNoTracking()
            .Where(t => t.ExecutionId == executionId && t.TenantId == tenantId)
            .OrderBy(t => t.TransitionAtUtc).ThenBy(t => t.CreatedAt)
            .Select(t => new LeadDiscoveryLockTransitionDto(
                t.Id, t.FromStatus.ToString(), t.ToStatus.ToString(), t.TransitionAtUtc, t.LockTokenReference, t.OwnerInstanceId, t.Reason, t.Error))
            .ToListAsync(cancellationToken);

        var run = await _context.LeadDiscoveryRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ExecutionId == executionId && r.TenantId == tenantId, cancellationToken);

        var pricing = await ResolvePricingAsync(tenantId, cancellationToken);

        var verificationCounts = new LeadDiscoveryVerificationCountsDto(
            customers.Count(c => c.VerificationStatus == nameof(LeadDiscoveryVerificationStatus.Pass)),
            customers.Count(c => c.VerificationStatus == nameof(LeadDiscoveryVerificationStatus.Fail)),
            customers.Count(c => c.VerificationStatus == nameof(LeadDiscoveryVerificationStatus.Pending)),
            customers.Count(c => c.VerificationStatus == nameof(LeadDiscoveryVerificationStatus.Error)),
            customers.Count(c => c.VerificationStatus == nameof(LeadDiscoveryVerificationStatus.NotChecked)));

        return new LeadDiscoveryExecutionDetailDto(
            ToSummary(execution, run, pricing),
            execution.LockKey,
            execution.LockTokenReference,
            execution.LockOwnerInstanceId,
            execution.LockAcquiredAtUtc,
            execution.LockExpiresAtUtc,
            execution.LockLastRenewedAtUtc,
            execution.ErrorDetails,
            execution.Summary,
            customers,
            templates,
            transitions,
            run is null ? null : LeadDiscoveryService.ToDto(run, pricing),
            pricing.CurrencyCode,
            pricing.CurrencySymbol,
            verificationCounts);
    }

    public async Task<LeadDiscoveryRetryQueuedDto> RequestRetryAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId();
        var execution = await _context.LeadDiscoveryExecutions.AsNoTracking()
                            .FirstOrDefaultAsync(e => e.Id == executionId && e.TenantId == tenantId, cancellationToken)
                        ?? throw new NotFoundException($"Lead discovery execution {executionId} was not found.");

        if (!LeadDiscoveryRetryRules.CanRetry(execution))
        {
            throw new ConflictException(execution.SupersededByExecutionId is { } next
                ? $"This execution was already retried by execution {next}."
                : $"A {execution.Status} execution cannot be retried.");
        }

        var jobId = _retryScheduler.EnqueueRetry(tenantId, executionId);
        return new LeadDiscoveryRetryQueuedDto(executionId, jobId);
    }

    private async Task<RegionalPricing> ResolvePricingAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var countryCode = await _context.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);
        return RegionalPricingCatalog.Resolve(countryCode);
    }

    private static LeadDiscoveryExecutionSummaryDto ToSummary(LeadDiscoveryExecution e, LeadDiscoveryRun? run, RegionalPricing pricing) => new(
        e.Id,
        DateTime.SpecifyKind(e.ProcessingDate, DateTimeKind.Unspecified),
        e.LeadDiscoveryProfileId,
        e.ProfileName,
        e.Trigger,
        e.Status.ToString(),
        AsUtc(e.StartedAtUtc),
        AsUtc(e.EndedAtUtc),
        e.LockStatus.ToString(),
        e.QuotaExhausted,
        e.CustomersDiscovered,
        e.CustomersCreated,
        e.CustomersDuplicate,
        e.CustomersInvalid,
        e.CustomersFailed,
        e.CustomersSkipped,
        e.AutoCampaignConfigured,
        e.AutoCampaignId,
        e.ReferredCampaignId,
        e.ReferredCampaignName,
        e.GeneratedCampaignId,
        e.GeneratedCampaignName,
        e.CampaignStatus.ToString(),
        e.CampaignNote,
        e.TemplateStatus.ToString(),
        e.MappingStatus.ToString(),
        e.MappingsEligible,
        e.MappingsCreated,
        e.MappingsExisting,
        e.MappingsFailed,
        e.RootExecutionId,
        e.RetryOfExecutionId,
        e.SupersededByExecutionId,
        e.RetryCount,
        AsUtc(e.LastRetryAtUtc),
        e.FailedStep,
        e.ErrorMessage,
        e.NextRetryInfo,
        LeadDiscoveryRetryRules.CanRetry(e),
        run is null ? 0m : LeadDiscoveryService.ToDto(run, pricing).EstimatedCostLocal,
        pricing.CurrencyCode,
        pricing.CurrencySymbol);

    // A DateTime read back from SQL Server datetime2 is Kind.Unspecified and would serialize without a "Z",
    // which a browser reads as local time.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? value) => value is { } v ? AsUtc(v) : null;

    private Guid CurrentTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Lead discovery history requires a tenant in scope.");
}
