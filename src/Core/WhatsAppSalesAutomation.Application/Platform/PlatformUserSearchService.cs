using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Auth;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformUserSearchService : IPlatformUserSearchService
{
    // A support lookup, not a listing - see IPlatformUserSearchService's own doc comment. Capped
    // rather than paged: if a search term matches more than this many accounts it's too broad to be
    // useful as a "which org is this email in" lookup, and the caller should narrow it instead.
    private const int MaxResults = 25;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IApplicationDbContext _context;
    private readonly IValidator<ResetUserPasswordRequest> _resetValidator;
    private readonly IAccountRecoveryService _recovery;
    private readonly IPlatformAuditService _auditService;

    public PlatformUserSearchService(
        UserManager<ApplicationUser> userManager,
        IApplicationDbContext context,
        IValidator<ResetUserPasswordRequest> resetValidator,
        IAccountRecoveryService recovery,
        IPlatformAuditService auditService)
    {
        _userManager = userManager;
        _context = context;
        _resetValidator = resetValidator;
        _recovery = recovery;
        _auditService = auditService;
    }

    public async Task ResetPasswordAsync(Guid userId, ResetUserPasswordRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        await _resetValidator.ValidateAndThrowAsync(request, cancellationToken);

        // IgnoreQueryFilters: the caller is a PlatformSuperAdmin with no ambient tenant.
        var user = await _userManager.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException(nameof(ApplicationUser), userId);

        if (user.TenantId is null)
            throw new ConflictException("Platform operator accounts can't be reset from here.");

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
            throw new ValidationException(result.Errors.Select(e =>
                new FluentValidation.Results.ValidationFailure(nameof(request.NewPassword), e.Description)));

        await _userManager.SetLockoutEndDateAsync(user, null);
        await _userManager.ResetAccessFailedCountAsync(user);
        await _recovery.RevokeAllSessionsAsync(user.Id, cancellationToken);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.UserPasswordReset, user.TenantId, user.Id,
            details: $"Reset the password of {user.Email}; all of their sessions were signed out.", cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<PlatformUserSearchResultDto>> SearchAsync(string? search, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(search))
            return Array.Empty<PlatformUserSearchResultDto>();

        var term = search.Trim();

        var matches = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.Email!.Contains(term) || u.FullName.Contains(term))
            .OrderBy(u => u.Email)
            .Take(MaxResults)
            .ToListAsync(cancellationToken);

        return await ToResultsAsync(matches, cancellationToken);
    }

    public async Task<IReadOnlyList<PlatformUserSearchResultDto>> ListForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!await _context.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            throw new NotFoundException(nameof(Tenant), tenantId);

        var users = await _userManager.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);

        return await ToResultsAsync(users, cancellationToken);
    }

    private async Task<IReadOnlyList<PlatformUserSearchResultDto>> ToResultsAsync(List<ApplicationUser> matches, CancellationToken cancellationToken)
    {
        if (matches.Count == 0)
            return Array.Empty<PlatformUserSearchResultDto>();

        var tenantIds = matches.Where(u => u.TenantId is not null).Select(u => u.TenantId!.Value).Distinct().ToList();
        var tenantNames = await _context.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var results = new List<PlatformUserSearchResultDto>(matches.Count);
        foreach (var user in matches)
        {
            var roles = await _userManager.GetRolesAsync(user);
            results.Add(new PlatformUserSearchResultDto(
                user.Id, user.Email ?? string.Empty, user.FullName,
                user.TenantId, user.TenantId is { } tenantId ? tenantNames.GetValueOrDefault(tenantId) : null,
                roles as IReadOnlyList<string> ?? roles.ToList(),
                user.IsActive, user.CreatedAt, user.LastLoginAt));
        }

        return results;
    }
}
