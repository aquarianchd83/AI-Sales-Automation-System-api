using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformTenantService : IPlatformTenantService
{
    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITenantWhatsAppConfigProvider _whatsAppConfigProvider;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantSlugResolver _slugResolver;
    private readonly ITenantJobProvisioner _jobProvisioner;
    private readonly IValidator<CreatePlatformTenantRequest> _createValidator;
    private readonly IValidator<UpdateTenantTimezoneRequest> _updateTimezoneValidator;
    private readonly IValidator<UpdateTenantCountryRequest> _updateCountryValidator;
    private readonly IPlatformAuditService _auditService;

    public PlatformTenantService(
        IApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        ITenantWhatsAppConfigProvider whatsAppConfigProvider,
        IJwtTokenService jwtTokenService,
        IDateTimeProvider dateTime,
        ITenantSlugResolver slugResolver,
        ITenantJobProvisioner jobProvisioner,
        IValidator<CreatePlatformTenantRequest> createValidator,
        IValidator<UpdateTenantTimezoneRequest> updateTimezoneValidator,
        IValidator<UpdateTenantCountryRequest> updateCountryValidator,
        IPlatformAuditService auditService)
    {
        _context = context;
        _userManager = userManager;
        _whatsAppConfigProvider = whatsAppConfigProvider;
        _jwtTokenService = jwtTokenService;
        _dateTime = dateTime;
        _slugResolver = slugResolver;
        _jobProvisioner = jobProvisioner;
        _createValidator = createValidator;
        _updateTimezoneValidator = updateTimezoneValidator;
        _updateCountryValidator = updateCountryValidator;
        _auditService = auditService;
    }

    public async Task<PagedResult<PlatformTenantListItemDto>> GetPagedAsync(PlatformTenantQuery query, CancellationToken cancellationToken = default)
    {
        var tenants = _context.Tenants.AsQueryable();

        if (query.Status is { } status)
            tenants = tenants.Where(t => t.Status == status);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            tenants = tenants.Where(t => t.Name.Contains(search) || t.Slug.Contains(search));
        }

        var totalCount = await tenants.CountAsync(cancellationToken);

        var page = await tenants
            .OrderByDescending(t => t.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(t => new { t.Id, t.Name, t.Slug, t.Status, t.CreatedAt, t.TrialEndsAtUtc })
            .ToListAsync(cancellationToken);

        var tenantIds = page.Select(t => t.Id).ToList();

        var userCounts = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId != null && tenantIds.Contains(u.TenantId!.Value))
            .GroupBy(u => u.TenantId!.Value)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

        var planNames = await GetPlanNamesByTenantAsync(tenantIds, cancellationToken);

        var items = page.Select(t => new PlatformTenantListItemDto(
                t.Id, t.Name, t.Slug, t.Status,
                planNames.GetValueOrDefault(t.Id),
                userCounts.GetValueOrDefault(t.Id),
                t.CreatedAt, t.TrialEndsAtUtc))
            .ToList();

        return new PagedResult<PlatformTenantListItemDto>(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<PlatformTenantDetailDto> GetDetailAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

        // IgnoreQueryFilters(), not FindByIdAsync - the caller here is a PlatformSuperAdmin (no ambient
        // tenant), and ApplicationUser's own query filter evaluates to "TenantId == null" for such a
        // caller, so a plain FindByIdAsync silently returns null for every tenant user. Same class of
        // bug this codebase already fixed once on ApplicationUser (see ApplyTenantQueryFilters' own
        // doc comment) - IgnoreQueryFilters() is the deliberate, audited exception, same as every
        // other cross-tenant read in this service.
        var ownerEmail = tenant.OwnerUserId is { } ownerId
            ? (await _userManager.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == ownerId, cancellationToken))?.Email
            : null;

        var userCount = await _userManager.Users.IgnoreQueryFilters()
            .CountAsync(u => u.TenantId == tenantId, cancellationToken);

        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        var plan = subscription?.PlanId is { } planId
            ? await _context.Plans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
            : null;

        var connections = await _whatsAppConfigProvider.GetAllConnectionSummariesAsync(cancellationToken);
        var connection = connections.FirstOrDefault(c => c.TenantId == tenantId);

        var now = _dateTime.UtcNow;
        var monthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var messagesSentThisMonth = await _context.Messages.IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.CreatedAt >= monthStartUtc, cancellationToken);

        var aiInteractionsThisMonth = await _context.AiInteractions.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.CreatedAt >= monthStartUtc)
            .Select(a => new { a.ModelUsed, a.PromptTokens, a.CompletionTokens })
            .ToListAsync(cancellationToken);

        var estimatedAiSpend = aiInteractionsThisMonth
            .Sum(a => AiSpendEstimator.EstimateUsd(a.ModelUsed, a.PromptTokens, a.CompletionTokens));

        // This tenant's own currency, not the operator's - the figures on this page are all about them.
        var pricing = RegionalPricingCatalog.Resolve(tenant.CountryCode);

        return new PlatformTenantDetailDto(
            tenant.Id, tenant.Name, tenant.Slug, tenant.Status, tenant.CreatedAt, tenant.TrialEndsAtUtc,
            tenant.OwnerUserId, ownerEmail, userCount,
            plan?.Name, subscription?.Status, subscription?.CurrentPeriodEndUtc,
            connection?.IsConnected ?? false,
            messagesSentThisMonth, plan?.MaxMessagesPerMonth,
            aiInteractionsThisMonth.Count, estimatedAiSpend,
            string.IsNullOrWhiteSpace(tenant.Timezone) ? TimeZoneCatalog.DefaultId : tenant.Timezone,
            tenant.CountryCode,
            pricing.CurrencyCode,
            pricing.CurrencySymbol,
            Math.Round(estimatedAiSpend * pricing.RateToUsd, 6, MidpointRounding.AwayFromZero));
    }

    public async Task<PlatformTenantDetailDto> CreateAsync(CreatePlatformTenantRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var existingUser = await _userManager.FindByEmailAsync(request.AdminEmail);
        if (existingUser is not null)
            throw new ConflictException($"A user with email '{request.AdminEmail}' already exists.");

        var slug = await _slugResolver.ResolveAsync(request.Slug, request.CompanyName, cancellationToken);

        var tenant = new Tenant
        {
            Name = request.CompanyName.Trim(),
            Slug = slug,
            Status = TenantStatus.Trial,
            TrialEndsAtUtc = _dateTime.UtcNow.AddDays(14),
            CountryCode = string.IsNullOrWhiteSpace(request.CountryCode) ? null : request.CountryCode.Trim().ToUpperInvariant(),
            // Same default as self-serve signup - see AuthService.SignUpAsync.
            Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? TimeZoneCatalog.DefaultId : request.Timezone
        };
        TenantBusinessDetails.ApplyTo(tenant, request);
        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync(cancellationToken);

        var user = new ApplicationUser
        {
            TenantId = tenant.Id,
            UserName = request.AdminEmail,
            Email = request.AdminEmail,
            FullName = request.AdminFullName.Trim(),
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = _dateTime.UtcNow
        };

        var result = await _userManager.CreateAsync(user, request.AdminPassword);
        if (!result.Succeeded)
            throw new ValidationException(result.Errors.Select(e => new FluentValidation.Results.ValidationFailure(nameof(request.AdminPassword), e.Description)));

        await _userManager.AddToRoleAsync(user, AppRoles.Admin);

        tenant.OwnerUserId = user.Id;
        await _context.SaveChangesAsync(cancellationToken);

        // Same reasoning as self-serve signup (see AuthService.SignUpAsync) - the tenant's background
        // jobs exist and are registered from the moment it does.
        await _jobProvisioner.SyncTenantAsync(tenant.Id, cancellationToken);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.TenantCreated, tenant.Id, user.Id,
            details: $"Created tenant '{tenant.Name}' ({tenant.Slug}) with admin {user.Email}", cancellationToken: cancellationToken);

        return await GetDetailAsync(tenant.Id, cancellationToken);
    }

    public async Task SuspendAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);
        tenant.Status = TenantStatus.Suspended;
        await _context.SaveChangesAsync(cancellationToken);

        // Unregisters every one of this tenant's recurring jobs immediately. Without this the suspension
        // would not actually stop anything until the next reconcile pass - a suspended tenant would keep
        // sending campaigns for up to an hour.
        await _jobProvisioner.SyncTenantAsync(tenantId, cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.TenantSuspended, tenantId, cancellationToken: cancellationToken);
    }

    public async Task ReactivateAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);
        tenant.Status = TenantStatus.Active;
        await _context.SaveChangesAsync(cancellationToken);

        // Re-registers the jobs the suspension removed, at whatever schedules this tenant had - the
        // TenantJobSchedules rows survive a suspension precisely so reactivating restores the operator's
        // own settings rather than the catalog defaults.
        await _jobProvisioner.SyncTenantAsync(tenantId, cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.TenantReactivated, tenantId, cancellationToken: cancellationToken);
    }

    public async Task DeleteAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);
        tenant.Status = TenantStatus.Deleted;
        await _context.SaveChangesAsync(cancellationToken);

        await _jobProvisioner.SyncTenantAsync(tenantId, cancellationToken);

        await _auditService.LogAsync(actorUserId, actorEmail, PlatformAuditActions.TenantDeleted, tenantId, cancellationToken: cancellationToken);
    }

    public async Task<ImpersonationSessionDto> ImpersonateAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);

        var target = tenant.OwnerUserId is { } ownerId
            ? await _userManager.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == ownerId && u.IsActive, cancellationToken)
            : null;

        if (target is null)
        {
            // Owner is gone/deactivated - fall back to the tenant's longest-standing active Admin
            // (falling back further to SuperAdmin) so a support session is still possible.
            target = await FindFallbackAdminAsync(tenantId, cancellationToken);
        }

        if (target is null)
            throw new NotFoundException($"Tenant '{tenant.Name}' has no active admin user to impersonate.");

        var roles = await _userManager.GetRolesAsync(target);
        var accessToken = _jwtTokenService.GenerateImpersonationAccessToken(target, roles.ToList(), actorUserId);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.TenantImpersonated, tenantId, target.Id,
            details: $"Impersonated {target.Email}", cancellationToken: cancellationToken);

        return new ImpersonationSessionDto(accessToken.Token, accessToken.ExpiresAtUtc, target.Id, target.Email ?? string.Empty, tenant.Id, tenant.Name);
    }

    public async Task OverridePlanAsync(Guid tenantId, OverrideTenantPlanRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);

        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == request.PlanId, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), request.PlanId);

        var subscription = await _context.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        var previousPlanId = subscription?.PlanId;

        if (subscription is null)
        {
            subscription = new Subscription { TenantId = tenantId, Status = SubscriptionStatus.Active };
            _context.Subscriptions.Add(subscription);
        }

        subscription.PlanId = plan.Id;
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.TenantPlanOverridden, tenantId,
            details: $"Plan {previousPlanId} -> {plan.Id} ({plan.Name})", cancellationToken: cancellationToken);
    }

    public async Task<TenantProfileDto> UpdateTimezoneAsync(
        Guid tenantId, UpdateTenantTimezoneRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        await _updateTimezoneValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);
        var previousTimezone = tenant.Timezone;
        tenant.Timezone = request.Timezone;
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.TenantTimezoneUpdated, tenantId,
            details: $"Timezone {previousTimezone ?? "(default)"} -> {request.Timezone}", cancellationToken: cancellationToken);

        return TenantProfileDto.From(tenant);
    }

    public async Task<TenantProfileDto> UpdateCountryAsync(
        Guid tenantId, UpdateTenantCountryRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        await _updateCountryValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);
        var previousCountry = tenant.CountryCode;
        tenant.CountryCode = request.CountryCode;
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.TenantCountryUpdated, tenantId,
            details: $"Country {previousCountry ?? "(none)"} -> {request.CountryCode}", cancellationToken: cancellationToken);

        return TenantProfileDto.From(tenant);
    }

    private async Task<Tenant> GetTenantOrThrowAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

    private async Task<ApplicationUser?> FindFallbackAdminAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var candidates = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.IsActive)
            .OrderBy(u => u.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (await _userManager.IsInRoleAsync(candidate, AppRoles.Admin))
                return candidate;
        }

        return null;
    }

    private async Task<Dictionary<Guid, string?>> GetPlanNamesByTenantAsync(IReadOnlyList<Guid> tenantIds, CancellationToken cancellationToken)
    {
        var subscriptions = await _context.Subscriptions.IgnoreQueryFilters()
            .Where(s => tenantIds.Contains(s.TenantId) && s.PlanId != null)
            .Select(s => new { s.TenantId, s.PlanId })
            .ToListAsync(cancellationToken);

        var planIds = subscriptions.Select(s => s.PlanId!.Value).Distinct().ToList();
        var plans = await _context.Plans.Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        return subscriptions.ToDictionary(s => s.TenantId, s => plans.GetValueOrDefault(s.PlanId!.Value));
    }
}
