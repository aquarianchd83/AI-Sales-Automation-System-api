using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Account;

/// <summary>See <see cref="IAccountProfileService"/>.</summary>
public class AccountProfileService : IAccountProfileService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ICurrentUserService _currentUser;
    private readonly IValidator<UpdateUserProfileRequest> _updateValidator;

    public AccountProfileService(
        UserManager<ApplicationUser> userManager,
        ICurrentUserService currentUser,
        IValidator<UpdateUserProfileRequest> updateValidator)
    {
        _userManager = userManager;
        _currentUser = currentUser;
        _updateValidator = updateValidator;
    }

    public async Task<UserProfileDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var user = await CurrentUserAsync();
        return await ToDtoAsync(user);
    }

    public async Task<UserProfileDto> UpdateAsync(UpdateUserProfileRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await CurrentUserAsync();

        var email = request.Email.Trim();
        if (!string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureEmailIsFreeAsync(email, user.Id, cancellationToken);

            // Both, not just the email: sign-in looks the account up by email, and Identity keeps the
            // normalized copies in step only through these two calls.
            await ThrowIfFailed(_userManager.SetEmailAsync(user, email));
            await ThrowIfFailed(_userManager.SetUserNameAsync(user, email));
        }

        user.FullName = request.FullName.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
        user.Timezone = request.Timezone;
        user.CountryCode = string.IsNullOrWhiteSpace(request.CountryCode) ? null : request.CountryCode.Trim();

        await ThrowIfFailed(_userManager.UpdateAsync(user));

        return await ToDtoAsync(user);
    }

    /// <summary>
    /// IgnoreQueryFilters() on purpose: ApplicationUser carries a tenant query filter, so an ordinary lookup
    /// only sees accounts in the caller's own tenant. Without this, an email already used in another tenant
    /// would pass the check here and then fail against Identity's unique index as a 500 instead of a
    /// validation message.
    /// </summary>
    private async Task EnsureEmailIsFreeAsync(string email, Guid userId, CancellationToken cancellationToken)
    {
        var normalized = _userManager.NormalizeEmail(email);

        var taken = await _userManager.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.NormalizedEmail == normalized && u.Id != userId, cancellationToken);

        if (taken)
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure(nameof(UpdateUserProfileRequest.Email), "That email address is already in use.")
            });
        }
    }

    private async Task<ApplicationUser> CurrentUserAsync()
    {
        var userId = _currentUser.UserId
            ?? throw new InvalidOperationException("Authenticated account-profile request has no user in scope.");

        return await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), userId);
    }

    private async Task<UserProfileDto> ToDtoAsync(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);

        return new UserProfileDto(
            user.Id,
            user.FullName,
            user.Email ?? string.Empty,
            user.PhoneNumber,
            // Effective value, never blank - the same "defaults to the platform timezone until set" contract
            // TenantProfileDto.Timezone has.
            string.IsNullOrWhiteSpace(user.Timezone) ? TimeZoneCatalog.DefaultId : user.Timezone,
            user.CountryCode,
            roles.ToList(),
            roles.Contains(AppRoles.PlatformSuperAdmin),
            user.TenantId,
            user.CreatedAt,
            user.LastLoginAt);
    }

    /// <summary>Identity reports its own failures as a result object rather than throwing; surface them as
    /// validation messages so the client sees why (e.g. a password policy or duplicate user name).</summary>
    private static async Task ThrowIfFailed(Task<IdentityResult> operation)
    {
        var result = await operation;
        if (result.Succeeded)
            return;

        throw new ValidationException(result.Errors
            .Select(e => new ValidationFailure(string.Empty, e.Description))
            .ToList());
    }
}
