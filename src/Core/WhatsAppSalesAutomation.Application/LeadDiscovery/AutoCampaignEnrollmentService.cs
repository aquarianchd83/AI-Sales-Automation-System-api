using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

/// <summary>What one discovered customer's auto-campaign enrollment attempt returns to
/// LeadDiscoveryRunService for its run summary. The actual outcome, including why, is always also
/// written to AutoCampaignEnrollment (see its own remarks) when the tenant has opted into the feature -
/// this return value only saves the caller a query to learn the tally.</summary>
public interface IAutoCampaignEnrollmentService
{
    /// <summary>
    /// Runs one freshly discovered, freshly saved customer through auto-campaign enrollment: if the
    /// tenant's LeadDiscoveryProfile has AutoCampaignEnabled with a usable SourceCampaignId, clones (or
    /// reuses today's already-cloned) execution campaign and attaches the customer to it; otherwise
    /// skips. Never throws - any failure is caught, recorded as a Failed AutoCampaignEnrollment row, and
    /// returned as <see cref="AutoCampaignEnrollmentStatus.Failed"/>, so one customer's campaign
    /// problem can never take down the discovery run it came from.
    /// </summary>
    Task<AutoCampaignEnrollmentStatus> EnrollAsync(
        Guid tenantId, DiscoveredLead lead, Guid customerId, CancellationToken cancellationToken = default);
}

public class AutoCampaignEnrollmentService : IAutoCampaignEnrollmentService
{
    /// <summary>Campaign.Name's column length (see CampaignConfiguration) - the generated
    /// "&lt;Source&gt;-&lt;date&gt;" name is truncated to fit, leaving room for a disambiguating suffix.</summary>
    private const int CampaignNameMaxLength = 200;

    private readonly IApplicationDbContext _context;
    private readonly ICampaignService _campaignService;
    private readonly IPlanLimitsService _planLimits;
    private readonly ITenantTimeZoneProvider _tenantTimeZone;

    public AutoCampaignEnrollmentService(
        IApplicationDbContext context,
        ICampaignService campaignService,
        IPlanLimitsService planLimits,
        ITenantTimeZoneProvider tenantTimeZone)
    {
        _context = context;
        _campaignService = campaignService;
        _planLimits = planLimits;
        _tenantTimeZone = tenantTimeZone;
    }

    public async Task<AutoCampaignEnrollmentStatus> EnrollAsync(
        Guid tenantId, DiscoveredLead lead, Guid customerId, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _context.LeadDiscoveryProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);

            // Not opted into this feature at all - nothing to audit. Every existing tenant reads this
            // way today (AutoCampaignEnabled defaults to false), so this must stay silent rather than
            // writing a row for every discovery, forever, for every tenant that never turned it on.
            if (profile is null || !profile.AutoCampaignEnabled)
                return AutoCampaignEnrollmentStatus.Skipped;

            var executionDate = (await _tenantTimeZone.GetLocalNowAsync(cancellationToken)).Date;

            if (profile.SourceCampaignId is not { } sourceCampaignId)
                return await RecordAsync(tenantId, null, null, lead, customerId, executionDate,
                    AutoCampaignEnrollmentStatus.Skipped, "Auto campaign is enabled but no source campaign is configured.",
                    cancellationToken);

            // Idempotency guard: a customer already Started for this source campaign (any day) is
            // never enrolled again, however many more times lead discovery runs.
            var alreadyEnrolled = await _context.AutoCampaignEnrollments.AsNoTracking()
                .AnyAsync(e => e.TenantId == tenantId && e.SourceCampaignId == sourceCampaignId
                            && e.CustomerId == customerId && e.Status == AutoCampaignEnrollmentStatus.Started,
                    cancellationToken);
            if (alreadyEnrolled)
                return await RecordAsync(tenantId, sourceCampaignId, null, lead, customerId, executionDate,
                    AutoCampaignEnrollmentStatus.Skipped, "Customer is already enrolled for this source campaign.",
                    cancellationToken);

            // AsNoTracking: read-only. This service must never modify the source campaign itself -
            // only ever clone from it.
            var source = await _context.Campaigns.AsNoTracking()
                .Include(c => c.Steps).ThenInclude(s => s.StepMedia)
                .FirstOrDefaultAsync(c => c.Id == sourceCampaignId && c.TenantId == tenantId, cancellationToken);

            if (source is null)
                return await RecordAsync(tenantId, sourceCampaignId, null, lead, customerId, executionDate,
                    AutoCampaignEnrollmentStatus.Skipped, "The configured source campaign no longer exists.",
                    cancellationToken);

            // Stopped is this platform's explicit "deactivated" - every other status (including Draft
            // and Completed) still has usable, clonable content. A Draft that isn't actually sendable
            // yet (no active Initial step / no Approved template) is caught below by StartAsync's own
            // validation instead of being pre-filtered here, so that failure reason is reported once,
            // in one place, the same way it is for a manual Start.
            if (source.Status == CampaignStatus.Stopped)
                return await RecordAsync(tenantId, sourceCampaignId, null, lead, customerId, executionDate,
                    AutoCampaignEnrollmentStatus.Skipped, $"Source campaign '{source.Name}' is Stopped.",
                    cancellationToken);

            // Same-day batching: every customer discovered for this source campaign on the same
            // tenant-local day shares one execution campaign, found via the newest Started row for
            // (tenant, source, day) rather than cloning a fresh one per customer.
            var reusableExecutionCampaignId = await _context.AutoCampaignEnrollments.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.SourceCampaignId == sourceCampaignId
                         && e.ExecutionDateLocal == executionDate && e.Status == AutoCampaignEnrollmentStatus.Started
                         && e.ExecutionCampaignId != null)
                .Select(e => e.ExecutionCampaignId)
                .FirstOrDefaultAsync(cancellationToken);

            Guid executionCampaignId;
            if (reusableExecutionCampaignId is { } reused)
            {
                executionCampaignId = reused;
            }
            else
            {
                Guid cloneId;
                try
                {
                    await _planLimits.EnsureCanCreateCampaignAsync(tenantId, cancellationToken);
                    cloneId = await CloneAsync(tenantId, source, executionDate, cancellationToken);
                }
                catch (Exception ex)
                {
                    return await RecordAsync(tenantId, sourceCampaignId, null, lead, customerId, executionDate,
                        AutoCampaignEnrollmentStatus.Failed, Truncate(ex.Message), cancellationToken);
                }

                try
                {
                    // Reuses CampaignService's own sendability validation (active Initial step, an
                    // Approved/active template) and its Scheduled-vs-Running promotion logic
                    // (ScheduledStartAt compared against tenant-local now) rather than reimplementing
                    // either here - see CampaignService.StartAsync/ValidateSendableAsync.
                    await _campaignService.StartAsync(cloneId, cancellationToken);
                }
                catch (Exception ex)
                {
                    // The clone stays as a Draft, on the record for an admin to fix or delete - not
                    // rolled back. The next customer discovered today will attempt its own clone
                    // (this failed one has no Started row, so it is never reused), which is noisy for
                    // a persistently broken source campaign but keeps every customer's own outcome
                    // individually audited, per this run's isolate-and-continue requirement.
                    return await RecordAsync(tenantId, sourceCampaignId, cloneId, lead, customerId, executionDate,
                        AutoCampaignEnrollmentStatus.Failed, Truncate(ex.Message), cancellationToken);
                }

                executionCampaignId = cloneId;
            }

            _context.CampaignCustomers.Add(new CampaignCustomer
            {
                TenantId = tenantId,
                CampaignId = executionCampaignId,
                CustomerId = customerId,
                Status = CampaignCustomerStatus.Pending
            });

            return await RecordAsync(tenantId, sourceCampaignId, executionCampaignId, lead, customerId, executionDate,
                AutoCampaignEnrollmentStatus.Started, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RecordAsync(tenantId, null, null, lead, customerId, DateTime.UtcNow.Date,
                AutoCampaignEnrollmentStatus.Failed, Truncate(ex.Message), cancellationToken);
        }
    }

    /// <summary>Deep-copies the source campaign's Steps and StepMedia into a new Draft campaign named
    /// "&lt;Source Campaign Name&gt;-&lt;executionDate:yyyy-MM-dd&gt;" (disambiguated if that name is
    /// already taken), with ScheduledStartAt moved to executionDate at the source's own configured
    /// time-of-day. Does not touch the source campaign itself.</summary>
    private async Task<Guid> CloneAsync(Guid tenantId, Campaign source, DateTime executionDate, CancellationToken cancellationToken)
    {
        var name = await BuildUniqueNameAsync(tenantId, source.Name, executionDate, cancellationToken);

        var scheduledStartAt = source.ScheduledStartAt is { } sourceSchedule
            ? executionDate.Date + sourceSchedule.TimeOfDay
            : (DateTime?)null;

        var clone = new Campaign
        {
            TenantId = tenantId,
            Name = name,
            Description = source.Description,
            Status = CampaignStatus.Draft,
            ScheduledStartAt = scheduledStartAt,
            CreatedBy = source.CreatedBy
        };
        _context.Campaigns.Add(clone);

        foreach (var step in source.Steps)
        {
            var clonedStep = new CampaignStep
            {
                TenantId = tenantId,
                CampaignId = clone.Id,
                StepType = step.StepType,
                StepNumber = step.StepNumber,
                DelayDaysAfterPrevious = step.DelayDaysAfterPrevious,
                MessageText = step.MessageText,
                MessageTemplateId = step.MessageTemplateId,
                IsActive = step.IsActive
            };
            clone.Steps.Add(clonedStep);
            _context.CampaignSteps.Add(clonedStep);

            var order = 0;
            foreach (var media in step.StepMedia.OrderBy(m => m.DisplayOrder))
            {
                var clonedMedia = new CampaignStepMedia
                {
                    TenantId = tenantId,
                    CampaignStepId = clonedStep.Id,
                    MediaAssetId = media.MediaAssetId,
                    DisplayOrder = order++
                };
                clonedStep.StepMedia.Add(clonedMedia);
                _context.CampaignStepMedia.Add(clonedMedia);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return clone.Id;
    }

    private async Task<string> BuildUniqueNameAsync(Guid tenantId, string sourceName, DateTime executionDate, CancellationToken cancellationToken)
    {
        var baseName = Truncate($"{sourceName}-{executionDate:yyyy-MM-dd}", CampaignNameMaxLength - 4)!;

        var existingNames = (await _context.Campaigns
                .Where(c => c.TenantId == tenantId && c.Name.StartsWith(baseName))
                .Select(c => c.Name)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        if (!existingNames.Contains(baseName))
            return baseName;

        // Same source campaign cloned more than once for the same tenant-local day - shouldn't happen
        // through this service's own same-day reuse, but a manually created campaign could already
        // hold the generated name. Disambiguate rather than fail the enrollment over a naming clash.
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName}-{suffix}";
            if (!existingNames.Contains(candidate))
                return candidate;
        }
    }

    private async Task<AutoCampaignEnrollmentStatus> RecordAsync(
        Guid tenantId, Guid? sourceCampaignId, Guid? executionCampaignId, DiscoveredLead lead, Guid customerId,
        DateTime executionDate, AutoCampaignEnrollmentStatus status, string? reason, CancellationToken cancellationToken)
    {
        _context.AutoCampaignEnrollments.Add(new AutoCampaignEnrollment
        {
            TenantId = tenantId,
            SourceCampaignId = sourceCampaignId,
            ExecutionCampaignId = executionCampaignId,
            DiscoveredLeadId = lead.Id,
            CustomerId = customerId,
            ExecutionDateLocal = executionDate,
            Status = status,
            Reason = reason
        });

        await _context.SaveChangesAsync(cancellationToken);
        return status;
    }

    private static string? Truncate(string? value, int maxLength = 1000) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;
}
