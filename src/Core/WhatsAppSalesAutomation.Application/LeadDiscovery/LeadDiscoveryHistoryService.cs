using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
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

        var days = pageDates
            .Select(date => new LeadDiscoveryHistoryDayDto(
                date,
                rows.Where(e => e.ProcessingDate == date).Select(ToSummary).ToList()))
            .ToList();

        return new PagedResult<LeadDiscoveryHistoryDayDto>(days, totalDays, query.Page, query.PageSize);
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
                c.ErrorMessage, c.ErrorDetails, c.RetriedFromId, c.ProcessedAtUtc))
            .ToListAsync(cancellationToken);

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

        return new LeadDiscoveryExecutionDetailDto(
            ToSummary(execution),
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
            transitions);
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

    private static LeadDiscoveryExecutionSummaryDto ToSummary(LeadDiscoveryExecution e) => new(
        e.Id,
        DateTime.SpecifyKind(e.ProcessingDate, DateTimeKind.Unspecified),
        e.LeadDiscoveryProfileId,
        e.ProfileName,
        e.Trigger,
        e.Status.ToString(),
        AsUtc(e.StartedAtUtc),
        AsUtc(e.EndedAtUtc),
        e.LockStatus.ToString(),
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
        LeadDiscoveryRetryRules.CanRetry(e));

    // A DateTime read back from SQL Server datetime2 is Kind.Unspecified and would serialize without a "Z",
    // which a browser reads as local time.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? value) => value is { } v ? AsUtc(v) : null;

    private Guid CurrentTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Lead discovery history requires a tenant in scope.");
}
