using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Auth;

/// <summary>
/// "Setup:BootstrapKey" - the shared secret that lets someone create the FIRST platform super admin from the
/// /setup page. Empty (the default) switches the setup page off entirely, so a deployed environment that never
/// sets it cannot be claimed by a stranger who finds the URL. Supply it by environment variable (Setup__BootstrapKey).
/// </summary>
public class PlatformSetupOptions
{
    public string BootstrapKey { get; set; } = string.Empty;
}

public record PlatformSetupStatusDto(bool SetupRequired, bool SetupEnabled);

public record CreatePlatformSuperAdminRequest(string FullName, string Email, string Password, string BootstrapKey);

public class CreatePlatformSuperAdminRequestValidator : AbstractValidator<CreatePlatformSuperAdminRequest>
{
    public CreatePlatformSuperAdminRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).MeetsPasswordPolicy();
        RuleFor(x => x.BootstrapKey).NotEmpty();
    }
}

public interface IPlatformSetupService
{
    Task<PlatformSetupStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the first PlatformSuperAdmin. One-shot: refused (409) as soon as any platform super admin exists.</summary>
    Task CreateFirstSuperAdminAsync(CreatePlatformSuperAdminRequest request, CancellationToken cancellationToken = default);
}

public class PlatformSetupService : IPlatformSetupService
{
    // Serialises concurrent first-run requests inside this process so two simultaneous submissions cannot both pass the
    // "no super admin yet" check.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IValidator<CreatePlatformSuperAdminRequest> _validator;
    private readonly IOptions<PlatformSetupOptions> _options;

    public PlatformSetupService(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IValidator<CreatePlatformSuperAdminRequest> validator,
        IOptions<PlatformSetupOptions> options)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _validator = validator;
        _options = options;
    }

    public async Task<PlatformSetupStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var required = !await SuperAdminExistsAsync();
        return new PlatformSetupStatusDto(required, SetupEnabled: !string.IsNullOrWhiteSpace(_options.Value.BootstrapKey));
    }

    public async Task CreateFirstSuperAdminAsync(CreatePlatformSuperAdminRequest request, CancellationToken cancellationToken = default)
    {
        var configuredKey = _options.Value.BootstrapKey;
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new FeatureDisabledException("Platform setup is not enabled on this server. Set Setup:BootstrapKey to enable it.");

        await _validator.ValidateAndThrowAsync(request, cancellationToken);

        if (!KeyMatches(configuredKey, request.BootstrapKey))
            throw new FeatureDisabledException("The setup key is not correct.");

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (await SuperAdminExistsAsync())
                throw new ConflictException("A platform super admin already exists, so setup is closed. Sign in instead.");

            if (await _userManager.FindByEmailAsync(request.Email) is not null)
                throw new ConflictException($"A user with email '{request.Email}' already exists.");

            if (!await _roleManager.RoleExistsAsync(AppRoles.PlatformSuperAdmin))
                await _roleManager.CreateAsync(new ApplicationRole(AppRoles.PlatformSuperAdmin));

            var admin = new ApplicationUser
            {
                TenantId = null,
                UserName = request.Email,
                Email = request.Email,
                FullName = request.FullName,
                EmailConfirmed = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            var created = await _userManager.CreateAsync(admin, request.Password);
            if (!created.Succeeded)
                throw new ValidationException(created.Errors.Select(e => new FluentValidation.Results.ValidationFailure(nameof(request.Password), e.Description)));

            var role = await _userManager.AddToRoleAsync(admin, AppRoles.PlatformSuperAdmin);
            if (!role.Succeeded)
            {
                // Never leave a super-admin-less account behind that blocks a retry with the same email.
                await _userManager.DeleteAsync(admin);
                throw new ValidationException(role.Errors.Select(e => new FluentValidation.Results.ValidationFailure(nameof(request.Email), e.Description)));
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<bool> SuperAdminExistsAsync()
        => (await _userManager.GetUsersInRoleAsync(AppRoles.PlatformSuperAdmin)).Count > 0;

    // Hash both sides first so the comparison is fixed-length and constant-time whatever the input length.
    private static bool KeyMatches(string expected, string supplied)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty)));
}
