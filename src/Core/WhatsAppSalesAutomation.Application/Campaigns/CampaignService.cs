using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Campaigns;

public class CampaignService : ICampaignService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantJobProvisioner _jobProvisioner;
    private readonly ILogger<CampaignService> _logger;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantContext _tenantContext;
    private readonly IPlanLimitsService _planLimits;
    private readonly ITenantConfigOverrideProvider _tenantConfig;
    private readonly ITenantTimeZoneProvider _tenantTimeZone;
    private readonly IValidator<CreateCampaignRequest> _createValidator;
    private readonly IValidator<UpdateCampaignRequest> _updateValidator;
    private readonly IValidator<UpsertCampaignStepRequest> _stepValidator;
    private readonly IValidator<SetCampaignAudienceRequest> _audienceValidator;

    public CampaignService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        ITenantContext tenantContext,
        IPlanLimitsService planLimits,
        ITenantConfigOverrideProvider tenantConfig,
        ITenantTimeZoneProvider tenantTimeZone,
        IValidator<CreateCampaignRequest> createValidator,
        IValidator<UpdateCampaignRequest> updateValidator,
        IValidator<UpsertCampaignStepRequest> stepValidator,
        IValidator<SetCampaignAudienceRequest> audienceValidator,
        ITenantJobProvisioner jobProvisioner,
        ILogger<CampaignService> logger)
    {
        _jobProvisioner = jobProvisioner;
        _logger = logger;
        _context = context;
        _dateTime = dateTime;
        _tenantContext = tenantContext;
        _planLimits = planLimits;
        _tenantConfig = tenantConfig;
        _tenantTimeZone = tenantTimeZone;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _stepValidator = stepValidator;
        _audienceValidator = audienceValidator;
    }

    public async Task<PagedResult<CampaignDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.Campaigns.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(c => c.Name.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var ids = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var dtos = new List<CampaignDto>(ids.Count);
        foreach (var id in ids)
            dtos.Add(await BuildDtoAsync(await LoadCampaignAsync(id, cancellationToken), cancellationToken));

        return new PagedResult<CampaignDto>(dtos, totalCount, request.Page, request.PageSize);
    }

    public async Task<CampaignDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await BuildDtoAsync(await LoadCampaignAsync(id, cancellationToken), cancellationToken);

    public async Task<CampaignDto> CreateAsync(CreateCampaignRequest request, Guid createdBy, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        if (_tenantContext.TenantId is { } tenantId)
            await _planLimits.EnsureCanCreateCampaignAsync(tenantId, cancellationToken);

        var campaign = new Campaign
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            ScheduledStartAt = request.ScheduledStartAt,
            CreatedBy = createdBy,
            Status = CampaignStatus.Draft
        };

        _context.Campaigns.Add(campaign);
        await _context.SaveChangesAsync(cancellationToken);

        // The campaign jobs (initial sends, follow-ups, retries, completion) do not exist until the tenant has a
        // campaign - this is the moment they are created.
        await SyncCampaignJobsAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    /// <summary>Brings the tenant's recurring jobs in line with whether it has a campaign. Never fails the caller: the
    /// campaign itself is already saved, and the daily reconcile puts the jobs right if Hangfire was unavailable.</summary>
    private async Task SyncCampaignJobsAsync(CancellationToken cancellationToken)
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return;

        try
        {
            await _jobProvisioner.SyncTenantAsync(tenantId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not update the campaign jobs of tenant {TenantId} after a campaign change; the next reconcile will", tenantId);
        }
    }

    public async Task<CampaignDto> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var campaign = await LoadCampaignAsync(id, cancellationToken);
        RequireStatus(campaign, "edit", CampaignStatus.Draft, CampaignStatus.Scheduled, CampaignStatus.Paused);

        campaign.Name = request.Name.Trim();
        campaign.Description = request.Description;
        campaign.ScheduledStartAt = request.ScheduledStartAt;

        // A Scheduled campaign with its date cleared would never be picked up again -
        // ProcessInitialSendsAsync's promotion query only matches ScheduledStartAt != null - so fall
        // back to Draft rather than leaving it stuck in Scheduled with nothing to promote it.
        if (campaign.Status == CampaignStatus.Scheduled && campaign.ScheduledStartAt is null)
            campaign.Status = CampaignStatus.Draft;

        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(id, cancellationToken);
        RequireStatus(campaign, "delete", CampaignStatus.Draft, CampaignStatus.Stopped, CampaignStatus.Completed);

        // Campaign -> CampaignCustomers cascades, but Message -> CampaignCustomer is deliberately
        // Restrict, not Cascade - a guard against any path silently destroying send history. A Draft
        // campaign never has Messages (nothing sends before Start), so this is a no-op there, but a
        // Stopped campaign usually does. Deleting one is an explicit, one-time exception to that
        // guard: the caller is choosing to discard the record of what was actually sent, not just the
        // campaign configuration, so the Messages have to go first or the cascade hits the Restrict
        // and fails with a raw DB foreign-key violation instead of completing.
        var campaignCustomerIds = await _context.CampaignCustomers
            .Where(cc => cc.CampaignId == id)
            .Select(cc => cc.Id)
            .ToListAsync(cancellationToken);

        if (campaignCustomerIds.Count > 0)
        {
            var messages = await _context.Messages
                .Where(m => m.CampaignCustomerId != null && campaignCustomerIds.Contains(m.CampaignCustomerId.Value))
                .ToListAsync(cancellationToken);

            _context.Messages.RemoveRange(messages);
        }

        _context.Campaigns.Remove(campaign);
        await _context.SaveChangesAsync(cancellationToken);

        // With its last campaign gone the tenant has nothing for those jobs to do again.
        await SyncCampaignJobsAsync(cancellationToken);
    }

    public async Task<CampaignDto> UpsertStepAsync(Guid campaignId, UpsertCampaignStepRequest request, CancellationToken cancellationToken = default)
    {
        await _stepValidator.ValidateAndThrowAsync(request, cancellationToken);

        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        RequireStatus(campaign, "edit steps on", CampaignStatus.Draft, CampaignStatus.Paused);

        // No upper bound on stepNumber - a campaign may carry any number of follow-ups. StepType
        // is still validated syntax ("Initial" or "FollowUp{N}") by _stepValidator; parsing again
        // here just recovers the number FluentValidation already confirmed is well-formed.
        CampaignStepTypeName.TryParse(request.StepType, out var stepNumber);
        var stepTypeName = CampaignStepTypeName.ForNumber(stepNumber);

        // A follow-up needs every earlier position already attached (Initial included) - otherwise
        // there is nothing wrong with the write itself, but the send pipeline walks the sequence one
        // step at a time and a gap it can never fill (no step exists there at all, active or not)
        // makes it give up and mark the customer Completed, silently dropping every follow-up after
        // the gap. Steps may still be deactivated later (CampaignStep.IsActive) without hitting this -
        // ProcessOneAsync in CampaignSendService is deliberately gap-tolerant for that case.
        if (stepNumber > 0)
        {
            var missing = Enumerable.Range(0, stepNumber)
                .Where(n => campaign.Steps.All(s => s.StepNumber != n))
                .Select(CampaignStepTypeName.ForNumber)
                .ToList();

            if (missing.Count > 0)
                throw Invalid("stepType", $"Attach {string.Join(", ", missing)} before {stepTypeName} - steps must be added in sequence.");
        }

        if (request.MediaAssetIds.Distinct().Count() != request.MediaAssetIds.Count)
            throw Invalid("mediaAssetIds", "Duplicate media asset ids.");

        // No min/max on a step's media: a message's picture now belongs to its template (MessageTemplate.HeaderMediaAssetId),
        // and step media is no longer attached to a send, so requiring some would only block a valid step. The ids
        // are still accepted and checked below so existing steps and older clients keep working.

        var mediaCount = await _context.MediaAssets.CountAsync(m => request.MediaAssetIds.Contains(m.Id), cancellationToken);
        if (mediaCount != request.MediaAssetIds.Count)
            throw Invalid("mediaAssetIds", "One or more media asset ids do not exist.");

        string? templateBody = null;
        if (request.MessageTemplateId.HasValue)
        {
            templateBody = await _context.MessageTemplates
                .Where(t => t.Id == request.MessageTemplateId)
                .Select(t => t.BodyText)
                .FirstOrDefaultAsync(cancellationToken);
            if (templateBody is null)
                throw Invalid("messageTemplateId", "Template does not exist.");
        }

        var step = campaign.Steps.FirstOrDefault(s => s.StepNumber == stepNumber);
        if (step is null)
        {
            step = new CampaignStep { CampaignId = campaign.Id, StepType = stepTypeName, StepNumber = stepNumber };
            campaign.Steps.Add(step);
            _context.CampaignSteps.Add(step);
        }
        else if (step.StepMedia.Count > 0)
        {
            // Flushed in its own SaveChanges before anything is re-added below (see the Add loop's
            // comment for the actual bug this sidesteps): re-submitting the same media on an edit
            // would otherwise pair a delete and an insert sharing the same (CampaignStepId,
            // MediaAssetId) unique key in one SaveChanges call. ToList() snapshots the collection
            // before RemoveRange/Clear both start mutating it.
            _context.CampaignStepMedia.RemoveRange(step.StepMedia.ToList());
            step.StepMedia.Clear();
            await _context.SaveChangesAsync(cancellationToken);
        }

        step.DelayDaysAfterPrevious = request.DelayDaysAfterPrevious;
        // A record of the template body as of this save (the column is required). The send itself reads the
        // template's live body, so this copy can lag behind a later template edit without affecting anything.
        step.MessageText = templateBody ?? request.MessageText ?? string.Empty;
        step.MessageTemplateId = request.MessageTemplateId;
        step.IsActive = request.IsActive;

        var order = 0;
        foreach (var mediaId in request.MediaAssetIds)
        {
            var stepMedia = new CampaignStepMedia { CampaignStepId = step.Id, MediaAssetId = mediaId, DisplayOrder = order++ };
            step.StepMedia.Add(stepMedia);

            // Explicit Add, not just the navigation-collection add above: CampaignStepMedia.Id is a
            // client-generated Guid (set in BaseEntity's property initializer, before EF ever sees
            // this object), so it is already non-default the moment it is constructed. When an entity
            // like that reaches the change tracker only via fixup from an already-tracked parent's
            // navigation (step here is Unchanged/Modified, not Added, on the edit path), EF Core reads
            // "non-default key, not explicitly Added" as "this must already exist" and generates an
            // UPDATE instead of an INSERT - which then fails with a 0-rows-affected concurrency
            // exception, since no such row exists yet. This never showed up on step creation, because
            // campaign.Steps.Add(step) a few lines up already puts the whole new-step graph in the
            // Added state. Calling Add explicitly here removes the ambiguity outright.
            _context.CampaignStepMedia.Add(stepMedia);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task<CampaignDto> RemoveStepAsync(Guid campaignId, string stepType, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        RequireStatus(campaign, "edit steps on", CampaignStatus.Draft, CampaignStatus.Paused);

        if (!CampaignStepTypeName.TryParse(stepType, out var parsedStepNumber))
            throw Invalid("stepType", $"'{stepType}' is not a valid step type. Use 'Initial' or 'FollowUp' followed by a positive number, e.g. 'FollowUp1'.");

        var step = campaign.Steps.FirstOrDefault(s => s.StepNumber == parsedStepNumber)
            ?? throw new NotFoundException("CampaignStep", stepType);

        // Mirrors the sequential-attach rule in UpsertStepAsync: removing a step out from under
        // later ones would open the same gap the send pipeline cannot fill on its own.
        var laterSteps = campaign.Steps.Where(s => s.StepNumber > step.StepNumber).Select(s => s.StepType).ToList();
        if (laterSteps.Count > 0)
            throw new ConflictException($"Remove {string.Join(", ", laterSteps)} first - steps must be removed from the end of the sequence.");

        _context.CampaignStepMedia.RemoveRange(step.StepMedia);
        _context.CampaignSteps.Remove(step);
        campaign.Steps.Remove(step);

        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task<SetCampaignAudienceResultDto> SetAudienceAsync(Guid campaignId, SetCampaignAudienceRequest request, CancellationToken cancellationToken = default)
    {
        await _audienceValidator.ValidateAndThrowAsync(request, cancellationToken);

        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        if (campaign.Status is CampaignStatus.Stopped or CampaignStatus.Completed)
            throw new ConflictException($"Campaign '{campaign.Name}' is {campaign.Status} and can no longer receive audience changes.");

        var candidates = _context.Customers.AsQueryable();

        var tagNames = request.TagNames?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
        var customerIds = request.CustomerIds ?? Array.Empty<Guid>();

        candidates = candidates.Where(c =>
            (tagNames.Count > 0 && c.Tags.Any(t => tagNames.Contains(t.Name))) ||
            (customerIds.Count > 0 && customerIds.Contains(c.Id)));

        var matched = await candidates
            .Select(c => new { c.Id, c.OptInStatus })
            .ToListAsync(cancellationToken);

        var alreadyAttached = (await _context.CampaignCustomers
                .Where(cc => cc.CampaignId == campaignId)
                .Select(cc => cc.CustomerId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var added = 0;
        var notOptedIn = 0;
        var alreadyAttachedCount = 0;

        foreach (var candidate in matched)
        {
            if (alreadyAttached.Contains(candidate.Id))
            {
                alreadyAttachedCount++;
                continue;
            }

            if (candidate.OptInStatus != OptInStatus.OptedIn)
            {
                notOptedIn++;
                continue;
            }

            _context.CampaignCustomers.Add(new CampaignCustomer
            {
                CampaignId = campaignId,
                CustomerId = candidate.Id,
                Status = CampaignCustomerStatus.Pending
            });
            alreadyAttached.Add(candidate.Id);
            added++;
        }

        campaign.TargetAudienceFilterJson = JsonSerializer.Serialize(new { TagNames = tagNames, CustomerIds = customerIds });

        await _context.SaveChangesAsync(cancellationToken);

        return new SetCampaignAudienceResultDto(matched.Count, added, alreadyAttachedCount, notOptedIn);
    }

    public async Task<CampaignDto> StartAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        RequireStatus(campaign, "start", CampaignStatus.Draft, CampaignStatus.Paused, CampaignStatus.Stopped);

        await ValidateSendableAsync(campaign, cancellationToken);

        // ScheduledStartAt is pinned to this tenant's own local time (see
        // Campaign.ScheduledStartAt), compared against ITenantTimeZoneProvider's tenant-aware "now",
        // not UtcNow. StartedAt is a true system timestamp and stays UTC.
        if (campaign.ScheduledStartAt is { } scheduled && scheduled > await _tenantTimeZone.GetLocalNowAsync(cancellationToken))
        {
            campaign.Status = CampaignStatus.Scheduled;
        }
        else
        {
            campaign.Status = CampaignStatus.Running;
            campaign.StartedAt ??= _dateTime.UtcNow;
        }

        // A resumed-from-Stopped campaign is no longer stopped - a stale StoppedAt would read as
        // "stopped at this timestamp" on a campaign that is, right now, Running/Scheduled again.
        // Does NOT revive individual CampaignCustomers that StopAsync force-completed - those keep
        // their Completed status and "Campaign stopped" reason as the historical record of what
        // actually happened to them; resuming only re-opens the campaign to newly-attached or
        // still-Pending customers, and to anyone re-attached to it after this call.
        campaign.StoppedAt = null;

        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task<CampaignDto> PauseAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        RequireStatus(campaign, "pause", CampaignStatus.Running, CampaignStatus.Scheduled);

        campaign.Status = CampaignStatus.Paused;
        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task<CampaignDto> ResumeAsync(Guid campaignId, CancellationToken cancellationToken = default) =>
        await StartAsync(campaignId, cancellationToken);

    public async Task<CampaignDto> StopAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        RequireStatus(campaign, "stop", CampaignStatus.Draft, CampaignStatus.Scheduled, CampaignStatus.Running, CampaignStatus.Paused);

        var now = _dateTime.UtcNow;
        campaign.Status = CampaignStatus.Stopped;
        campaign.StoppedAt = now;

        // Every in-flight customer stops here too - a stopped campaign must not keep sending
        // follow-ups that were already scheduled before Stop was called. Plain load-and-save rather
        // than ExecuteUpdateAsync - see the equivalent note in CampaignSendService.
        var inFlight = await _context.CampaignCustomers
            .Where(cc => cc.CampaignId == campaignId && cc.Status == CampaignCustomerStatus.AwaitingResponse)
            .ToListAsync(cancellationToken);

        foreach (var cc in inFlight)
        {
            cc.Status = CampaignCustomerStatus.Completed;
            cc.NextFollowUpDueAt = null;
            cc.StoppedReason = "Campaign stopped";
        }

        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task<CampaignProgressDto> GetProgressAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        await LoadCampaignAsync(campaignId, cancellationToken); // throws NotFoundException if missing

        var counts = await _context.CampaignCustomers
            .Where(cc => cc.CampaignId == campaignId)
            .GroupBy(cc => cc.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var byStatus = counts.ToDictionary(c => c.Status.ToString(), c => c.Count);

        return new CampaignProgressDto(campaignId, counts.Sum(c => c.Count), byStatus);
    }

    public async Task<PagedResult<CampaignAudienceMemberDto>> GetAudienceAsync(Guid campaignId, PagedRequest request, CancellationToken cancellationToken = default)
    {
        await LoadCampaignAsync(campaignId, cancellationToken); // throws NotFoundException if missing

        var query =
            from cc in _context.CampaignCustomers
            join c in _context.Customers on cc.CustomerId equals c.Id
            where cc.CampaignId == campaignId
            select new { cc, c };

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x =>
                x.c.PhoneNumberE164.Contains(search) ||
                (x.c.FirstName != null && x.c.FirstName.Contains(search)) ||
                (x.c.LastName != null && x.c.LastName.Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.cc.LastMessageSentAt ?? DateTime.MinValue)
            .ThenBy(x => x.c.PhoneNumberE164)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new CampaignAudienceMemberDto(
                x.c.Id,
                x.c.PhoneNumberE164,
                x.c.FirstName,
                x.c.LastName,
                x.cc.Status.ToString(),
                x.cc.CurrentStepNumber,
                x.cc.LastMessageSentAt,
                x.cc.NextFollowUpDueAt,
                x.cc.StoppedReason))
            .ToListAsync(cancellationToken);

        return new PagedResult<CampaignAudienceMemberDto>(items, totalCount, request.Page, request.PageSize);
    }

    public async Task<PagedResult<CampaignMessageHistoryEntryDto>> GetHistoryAsync(Guid campaignId, CampaignHistoryQuery query, CancellationToken cancellationToken = default)
    {
        await LoadCampaignAsync(campaignId, cancellationToken); // throws NotFoundException if missing

        var campaignCustomerIds = _context.CampaignCustomers.Where(cc => cc.CampaignId == campaignId).Select(cc => cc.Id);

        var messages =
            from m in _context.Messages
            join c in _context.Customers on m.CustomerId equals c.Id
            where m.CampaignCustomerId != null && campaignCustomerIds.Contains(m.CampaignCustomerId.Value)
            select new { m, c };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            messages = messages.Where(x =>
                x.c.PhoneNumberE164.Contains(search) ||
                (x.c.FirstName != null && x.c.FirstName.Contains(search)) ||
                (x.c.LastName != null && x.c.LastName.Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<MessageStatus>(query.Status, ignoreCase: true, out var status))
            messages = messages.Where(x => x.m.Status == status);

        if (query.StepNumber is { } stepNumber)
            messages = messages.Where(x => x.m.CampaignStepNumber == stepNumber);

        if (!string.IsNullOrWhiteSpace(query.TemplateName))
            messages = messages.Where(x => x.m.TemplateName == query.TemplateName);

        // Calendar-day bounds, not literal timestamps - From/To arrive as "YYYY-MM-DD" with no
        // time-of-day, and Message.CreatedAt (unlike CampaignCustomer's date-only equivalents
        // elsewhere) carries one, so an exact "<= to" would silently drop everything sent after
        // midnight on the end date.
        if (query.From is { } from)
            messages = messages.Where(x => x.m.CreatedAt >= from.Date);
        if (query.To is { } to)
            messages = messages.Where(x => x.m.CreatedAt < to.Date.AddDays(1));

        var totalCount = await messages.CountAsync(cancellationToken);

        var items = await messages
            .OrderByDescending(x => x.m.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new CampaignMessageHistoryEntryDto(
                x.m.Id,
                x.c.Id,
                x.c.PhoneNumberE164,
                x.c.FirstName,
                x.c.LastName,
                x.m.CampaignStepNumber,
                x.m.TemplateName,
                x.m.Text,
                x.m.Status.ToString(),
                x.m.FailureReason,
                x.m.SentAt,
                x.m.DeliveredAt,
                x.m.ReadAt,
                x.m.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<CampaignMessageHistoryEntryDto>(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<CampaignStepDeliverySummaryDto>> GetStepDeliverySummaryAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        var templateIds = campaign.Steps.Where(s => s.MessageTemplateId.HasValue).Select(s => s.MessageTemplateId!.Value).Distinct().ToList();
        var templateNames = await _context.MessageTemplates
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var summaries = new List<CampaignStepDeliverySummaryDto>();
        foreach (var step in campaign.Steps.OrderBy(s => s.StepNumber))
        {
            var rows = await LoadStepRecipientsAsync(campaignId, step.StepNumber, null, cancellationToken);
            int Count(string outcome) => rows.Count(r => r.Outcome == outcome);

            summaries.Add(new CampaignStepDeliverySummaryDto(
                step.StepNumber,
                step.StepType,
                step.MessageTemplateId is { } tid && templateNames.TryGetValue(tid, out var name) ? name : null,
                step.IsActive,
                rows.Count,
                Count(CampaignStepOutcome.Queued),
                Count(CampaignStepOutcome.Sent),
                Count(CampaignStepOutcome.Delivered),
                Count(CampaignStepOutcome.Read),
                Count(CampaignStepOutcome.Failed),
                Count(CampaignStepOutcome.Upcoming),
                Count(CampaignStepOutcome.WillNotReceive)));
        }

        return summaries;
    }

    public async Task<PagedResult<CampaignStepRecipientDto>> GetStepRecipientsAsync(Guid campaignId, int stepNumber, CampaignStepRecipientQuery query, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        if (campaign.Steps.All(s => s.StepNumber != stepNumber))
            throw new NotFoundException(nameof(CampaignStep), stepNumber);

        var rows = await LoadStepRecipientsAsync(campaignId, stepNumber, query.Search, cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            var outcome = query.Outcome.Trim();
            rows = rows.Where(r => string.Equals(r.Outcome, outcome, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Failures first (they are the ones needing action), then whoever is still due, then the rest.
        var items = rows
            .OrderBy(r => OutcomeRank(r.Outcome))
            .ThenBy(r => r.FirstName)
            .ThenBy(r => r.PhoneNumberE164)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        return new PagedResult<CampaignStepRecipientDto>(items, rows.Count, query.Page, query.PageSize);
    }

    private static int OutcomeRank(string outcome) => outcome switch
    {
        CampaignStepOutcome.Failed => 0,
        CampaignStepOutcome.Queued => 1,
        CampaignStepOutcome.Upcoming => 2,
        CampaignStepOutcome.Sent => 3,
        CampaignStepOutcome.Delivered => 4,
        CampaignStepOutcome.Read => 5,
        _ => 6,
    };

    /// <summary>The whole audience's outcome for one step. Built in memory from two narrow queries: a
    /// campaign audience is bounded by the plan's customer limit, and the outcome rule (message status,
    /// else "will it still be sent?") does not translate cleanly to SQL. A step has at most one message
    /// per campaign customer - the send idempotency key is (campaign customer, step).</summary>
    private async Task<List<CampaignStepRecipientDto>> LoadStepRecipientsAsync(Guid campaignId, int stepNumber, string? search, CancellationToken cancellationToken)
    {
        var members = _context.CampaignCustomers
            .Where(cc => cc.CampaignId == campaignId)
            .Join(_context.Customers, cc => cc.CustomerId, c => c.Id, (cc, c) => new { cc, c });

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            members = members.Where(x =>
                x.c.PhoneNumberE164.Contains(term) ||
                (x.c.FirstName != null && x.c.FirstName.Contains(term)) ||
                (x.c.LastName != null && x.c.LastName.Contains(term)));
        }

        var audience = await members
            .Select(x => new
            {
                CampaignCustomerId = x.cc.Id,
                x.cc.Status,
                x.cc.CurrentStepNumber,
                x.cc.NextFollowUpDueAt,
                x.cc.StoppedReason,
                CustomerId = x.c.Id,
                x.c.PhoneNumberE164,
                x.c.FirstName,
                x.c.LastName,
            })
            .ToListAsync(cancellationToken);

        var campaignCustomerIds = _context.CampaignCustomers.Where(cc => cc.CampaignId == campaignId).Select(cc => cc.Id);
        var messages = (await _context.Messages
                .Where(m => m.CampaignStepNumber == stepNumber && m.CampaignCustomerId != null && campaignCustomerIds.Contains(m.CampaignCustomerId.Value))
                .Select(m => new { m.Id, CampaignCustomerId = m.CampaignCustomerId!.Value, m.Status, m.FailureReason, m.SentAt, m.DeliveredAt, m.ReadAt, m.CreatedAt })
                .ToListAsync(cancellationToken))
            .GroupBy(m => m.CampaignCustomerId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());

        return audience.Select(a =>
        {
            if (messages.TryGetValue(a.CampaignCustomerId, out var m))
            {
                return new CampaignStepRecipientDto(
                    a.CustomerId, a.PhoneNumberE164, a.FirstName, a.LastName,
                    m.Status.ToString(), m.Id, m.FailureReason, m.SentAt, m.DeliveredAt, m.ReadAt, null, null);
            }

            var stillDue = a.Status is CampaignCustomerStatus.Pending or CampaignCustomerStatus.AwaitingResponse
                && a.CurrentStepNumber < stepNumber;

            return stillDue
                ? new CampaignStepRecipientDto(
                    a.CustomerId, a.PhoneNumberE164, a.FirstName, a.LastName,
                    CampaignStepOutcome.Upcoming, null, null, null, null, null,
                    a.CurrentStepNumber == stepNumber - 1 ? a.NextFollowUpDueAt : null, null)
                : new CampaignStepRecipientDto(
                    a.CustomerId, a.PhoneNumberE164, a.FirstName, a.LastName,
                    CampaignStepOutcome.WillNotReceive, null, null, null, null, null, null,
                    a.StoppedReason ?? a.Status.ToString());
        }).ToList();
    }

    private async Task ValidateSendableAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        var initial = campaign.Steps.FirstOrDefault(s => s.StepNumber == 0 && s.IsActive);
        if (initial is null)
            throw new ConflictException($"Campaign '{campaign.Name}' has no active Initial step.");

        var activeSteps = campaign.Steps.Where(s => s.IsActive).ToList();
        var templateIds = activeSteps.Select(s => s.MessageTemplateId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

        var templates = await _context.MessageTemplates
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        foreach (var step in activeSteps)
        {
            if (step.MessageTemplateId is null || !templates.TryGetValue(step.MessageTemplateId.Value, out var template))
                throw new ConflictException($"Step '{step.StepType}' has no template assigned.");

            if (template.WhatsAppTemplateStatus != WhatsAppTemplateStatus.Approved || !template.IsActive)
                throw new ConflictException($"Step '{step.StepType}' references template '{template.Name}', which is not an active, Approved template.");
        }
    }

    private static void RequireStatus(Campaign campaign, string action, params CampaignStatus[] allowed)
    {
        if (!allowed.Contains(campaign.Status))
            throw new ConflictException($"Cannot {action} campaign '{campaign.Name}' while it is {campaign.Status}. Allowed: {string.Join(", ", allowed)}.");
    }

    private async Task<Campaign> LoadCampaignAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.Campaigns
            .Include(c => c.Steps).ThenInclude(s => s.StepMedia)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
        ?? throw new NotFoundException(nameof(Campaign), id);

    private async Task<CampaignDto> BuildDtoAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        var audienceCount = await _context.CampaignCustomers.CountAsync(cc => cc.CampaignId == campaign.Id, cancellationToken);

        var templateIds = campaign.Steps.Where(s => s.MessageTemplateId.HasValue).Select(s => s.MessageTemplateId!.Value).Distinct().ToList();
        var templateNames = await _context.MessageTemplates
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        return campaign.ToDto(audienceCount, templateNames);
    }

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
