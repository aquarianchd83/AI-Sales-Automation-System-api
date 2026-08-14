using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Campaigns;

public class CampaignService : ICampaignService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly CampaignOptions _options;
    private readonly IValidator<CreateCampaignRequest> _createValidator;
    private readonly IValidator<UpdateCampaignRequest> _updateValidator;
    private readonly IValidator<UpsertCampaignStepRequest> _stepValidator;
    private readonly IValidator<SetCampaignAudienceRequest> _audienceValidator;

    public CampaignService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IOptions<CampaignOptions> options,
        IValidator<CreateCampaignRequest> createValidator,
        IValidator<UpdateCampaignRequest> updateValidator,
        IValidator<UpsertCampaignStepRequest> stepValidator,
        IValidator<SetCampaignAudienceRequest> audienceValidator)
    {
        _context = context;
        _dateTime = dateTime;
        _options = options.Value;
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

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task<CampaignDto> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var campaign = await LoadCampaignAsync(id, cancellationToken);
        RequireStatus(campaign, "edit", CampaignStatus.Draft);

        campaign.Name = request.Name.Trim();
        campaign.Description = request.Description;
        campaign.ScheduledStartAt = request.ScheduledStartAt;

        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(id, cancellationToken);
        RequireStatus(campaign, "delete", CampaignStatus.Draft);

        _context.Campaigns.Remove(campaign);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<CampaignDto> UpsertStepAsync(Guid campaignId, UpsertCampaignStepRequest request, CancellationToken cancellationToken = default)
    {
        await _stepValidator.ValidateAndThrowAsync(request, cancellationToken);

        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        RequireStatus(campaign, "edit steps on", CampaignStatus.Draft, CampaignStatus.Paused);

        var stepType = Enum.Parse<CampaignStepType>(request.StepType, ignoreCase: true);
        var stepNumber = (int)stepType;

        if (request.MediaAssetIds.Distinct().Count() != request.MediaAssetIds.Count)
            throw Invalid("mediaAssetIds", "Duplicate media asset ids.");

        if (request.MediaAssetIds.Count < _options.MinStepMedia || request.MediaAssetIds.Count > _options.MaxStepMedia)
            throw Invalid("mediaAssetIds", $"A step needs between {_options.MinStepMedia} and {_options.MaxStepMedia} media items; {request.MediaAssetIds.Count} given.");

        var mediaCount = await _context.MediaAssets.CountAsync(m => request.MediaAssetIds.Contains(m.Id), cancellationToken);
        if (mediaCount != request.MediaAssetIds.Count)
            throw Invalid("mediaAssetIds", "One or more media asset ids do not exist.");

        if (request.MessageTemplateId.HasValue)
        {
            var templateExists = await _context.MessageTemplates.AnyAsync(t => t.Id == request.MessageTemplateId, cancellationToken);
            if (!templateExists)
                throw Invalid("messageTemplateId", "Template does not exist.");
        }

        var step = campaign.Steps.FirstOrDefault(s => s.StepNumber == stepNumber);
        if (step is null)
        {
            step = new CampaignStep { CampaignId = campaign.Id, StepType = stepType, StepNumber = stepNumber };
            campaign.Steps.Add(step);
            _context.CampaignSteps.Add(step);
        }
        else
        {
            _context.CampaignStepMedia.RemoveRange(step.StepMedia);
            step.StepMedia.Clear();
        }

        step.DelayDaysAfterPrevious = request.DelayDaysAfterPrevious;
        step.MessageText = request.MessageText;
        step.MessageTemplateId = request.MessageTemplateId;
        step.IsActive = request.IsActive;

        var order = 0;
        foreach (var mediaId in request.MediaAssetIds)
            step.StepMedia.Add(new CampaignStepMedia { CampaignStepId = step.Id, MediaAssetId = mediaId, DisplayOrder = order++ });

        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(campaign, cancellationToken);
    }

    public async Task<CampaignDto> RemoveStepAsync(Guid campaignId, string stepType, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadCampaignAsync(campaignId, cancellationToken);
        RequireStatus(campaign, "edit steps on", CampaignStatus.Draft, CampaignStatus.Paused);

        if (!Enum.TryParse<CampaignStepType>(stepType, ignoreCase: true, out var parsed))
            throw Invalid("stepType", $"StepType must be one of: {string.Join(", ", Enum.GetNames<CampaignStepType>())}.");

        var step = campaign.Steps.FirstOrDefault(s => s.StepNumber == (int)parsed)
            ?? throw new NotFoundException("CampaignStep", stepType);

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
        RequireStatus(campaign, "start", CampaignStatus.Draft, CampaignStatus.Paused);

        await ValidateSendableAsync(campaign, cancellationToken);

        var now = _dateTime.UtcNow;
        if (campaign.ScheduledStartAt is { } scheduled && scheduled > now)
        {
            campaign.Status = CampaignStatus.Scheduled;
        }
        else
        {
            campaign.Status = CampaignStatus.Running;
            campaign.StartedAt ??= now;
        }

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
            if (step.StepMedia.Count < _options.MinStepMedia || step.StepMedia.Count > _options.MaxStepMedia)
                throw new ConflictException($"Step '{step.StepType}' needs between {_options.MinStepMedia} and {_options.MaxStepMedia} media items; it has {step.StepMedia.Count}.");

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
