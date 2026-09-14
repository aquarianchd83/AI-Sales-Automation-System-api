using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformBillingService : IPlatformBillingService
{
    private readonly IApplicationDbContext _context;
    private readonly IValidator<CreatePlanRequest> _createValidator;
    private readonly IValidator<UpdatePlanRequest> _updateValidator;

    public PlatformBillingService(
        IApplicationDbContext context,
        IValidator<CreatePlanRequest> createValidator,
        IValidator<UpdatePlanRequest> updateValidator)
    {
        _context = context;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<IReadOnlyList<PlatformPlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Plans
            .OrderBy(p => p.PriceMonthlyCents)
            .Select(p => new PlatformPlanDto(
                p.Id, p.Code, p.Name, p.MaxUsers, p.MaxMessagesPerMonth,
                p.MaxCampaigns, p.MaxKnowledgeBaseArticles, p.PriceMonthlyCents, p.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<PlatformSubscriptionListItemDto>> GetSubscriptionsAsync(PlatformSubscriptionQuery query, CancellationToken cancellationToken = default)
    {
        var subscriptions = _context.Subscriptions.IgnoreQueryFilters().AsQueryable();

        if (query.Status is { } status)
            subscriptions = subscriptions.Where(s => s.Status == status);

        var joined = subscriptions
            .Join(_context.Tenants, s => s.TenantId, t => t.Id, (s, t) => new { s, t })
            .GroupJoin(_context.Plans, x => x.s.PlanId, p => (Guid?)p.Id, (x, plans) => new { x.s, x.t, plans })
            .SelectMany(x => x.plans.DefaultIfEmpty(), (x, p) => new { x.s, x.t, p });

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            joined = joined.Where(x => x.t.Name.Contains(search) || x.t.Slug.Contains(search));
        }

        var totalCount = await joined.CountAsync(cancellationToken);

        var items = await joined
            .OrderByDescending(x => x.s.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new PlatformSubscriptionListItemDto(
                x.t.Id, x.t.Name, x.p == null ? null : x.p.Name, x.s.Status, x.s.CurrentPeriodEndUtc, x.s.Status == SubscriptionStatus.PastDue))
            .ToListAsync(cancellationToken);

        return new PagedResult<PlatformSubscriptionListItemDto>(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<PlatformPlanDto> CreatePlanAsync(CreatePlanRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var plan = new Plan
        {
            Code = request.Code.Trim().ToLowerInvariant(),
            Name = request.Name.Trim(),
            MaxUsers = request.MaxUsers,
            MaxMessagesPerMonth = request.MaxMessagesPerMonth,
            MaxCampaigns = request.MaxCampaigns,
            MaxKnowledgeBaseArticles = request.MaxKnowledgeBaseArticles,
            PriceMonthlyCents = request.PriceMonthlyCents,
            IsActive = true
        };
        _context.Plans.Add(plan);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(plan);
    }

    public async Task<PlatformPlanDto> UpdatePlanAsync(Guid id, UpdatePlanRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), id);

        plan.Name = request.Name.Trim();
        plan.MaxUsers = request.MaxUsers;
        plan.MaxMessagesPerMonth = request.MaxMessagesPerMonth;
        plan.MaxCampaigns = request.MaxCampaigns;
        plan.MaxKnowledgeBaseArticles = request.MaxKnowledgeBaseArticles;
        plan.PriceMonthlyCents = request.PriceMonthlyCents;
        plan.IsActive = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(plan);
    }

    public async Task DeactivatePlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), id);

        if (!plan.IsActive)
            return;

        plan.IsActive = false;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static PlatformPlanDto ToDto(Plan p) => new(
        p.Id, p.Code, p.Name, p.MaxUsers, p.MaxMessagesPerMonth,
        p.MaxCampaigns, p.MaxKnowledgeBaseArticles, p.PriceMonthlyCents, p.IsActive);
}
