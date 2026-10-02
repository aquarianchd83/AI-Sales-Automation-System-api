using WhatsAppSalesAutomation.Application.Billing;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Leads;
using WhatsAppSalesAutomation.Application.Tenancy;
using WhatsAppSalesAutomation.Application.Users;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Auth;

public class AuthService : IAuthService
{
    public const string LockedOutMessage = "Too many failed sign-in attempts. Try again in a few minutes, or reset your password.";

    // A real hash to verify against when the email has no account, so "unknown email" costs the same time as
    // "wrong password" and response timing cannot be used to learn which emails are registered.
    private static readonly ApplicationUser DecoyUser = new();
    private static readonly string DecoyHash = new PasswordHasher<ApplicationUser>().HashPassword(DecoyUser, "Decoy-Password-1!");

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IApplicationDbContext _context;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantSlugResolver _slugResolver;
    private readonly ITenantJobProvisioner _jobProvisioner;
    private readonly IQuotaGate _quota;
    private readonly IQualificationAdminService _qualification;
    private readonly ILeadScoringAdminService _leadScoring;
    private readonly IValidator<TenantSignUpRequest> _signUpValidator;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IValidator<RefreshTokenRequest> _refreshTokenValidator;
    private readonly IValidator<ChangePasswordRequest> _changePasswordValidator;

    private readonly ICountryAvailability _countries;
    private readonly IPlatformNotifier _platformNotifier;
    private readonly IAccountRecoveryService _recovery;
    private readonly ITenantNotifier _tenantNotifier;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        IApplicationDbContext context,
        IJwtTokenService jwtTokenService,
        IDateTimeProvider dateTime,
        ITenantSlugResolver slugResolver,
        ITenantJobProvisioner jobProvisioner,
        IQualificationAdminService qualification,
        ILeadScoringAdminService leadScoring,
        IQuotaGate quota,
        IValidator<TenantSignUpRequest> signUpValidator,
        IValidator<LoginRequest> loginValidator,
        IValidator<RefreshTokenRequest> refreshTokenValidator,
        IValidator<ChangePasswordRequest> changePasswordValidator,
        ICountryAvailability countries,
        IPlatformNotifier platformNotifier,
        IAccountRecoveryService recovery,
        ITenantNotifier tenantNotifier)
    {
        _tenantNotifier = tenantNotifier;
        _recovery = recovery;
        _countries = countries;
        _platformNotifier = platformNotifier;
        _userManager = userManager;
        _context = context;
        _jwtTokenService = jwtTokenService;
        _dateTime = dateTime;
        _slugResolver = slugResolver;
        _jobProvisioner = jobProvisioner;
        _quota = quota;
        _qualification = qualification;
        _leadScoring = leadScoring;
        _signUpValidator = signUpValidator;
        _loginValidator = loginValidator;
        _refreshTokenValidator = refreshTokenValidator;
        _changePasswordValidator = changePasswordValidator;
    }

    public async Task<TokenPairDto> SignUpAsync(TenantSignUpRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        await _signUpValidator.ValidateAndThrowAsync(request, cancellationToken);

        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser is not null)
            throw new ConflictException($"A user with email '{request.Email}' already exists.");

        var slug = await _slugResolver.ResolveAsync(request.Slug, request.CompanyName, cancellationToken);

        await _countries.EnsureAllowedAsync(request.CountryCode, null, cancellationToken);

        var tenant = new Tenant
        {
            Name = request.CompanyName,
            Slug = slug,
            Status = TenantStatus.Trial,
            TrialEndsAtUtc = _dateTime.UtcNow.AddDays(14),
            CountryCode = string.IsNullOrWhiteSpace(request.CountryCode) ? null : request.CountryCode.Trim().ToUpperInvariant(),
            StateCode = IndianStates.AppliesTo(request.CountryCode) && !string.IsNullOrWhiteSpace(request.StateCode) ? request.StateCode.Trim().ToUpperInvariant() : null,
            // Unlike CountryCode (no universal default makes sense there), every tenant gets an
            // explicit Timezone from creation - seeded to the platform default (IST) when the signup
            // form didn't collect one, the same value ITenantTimeZoneProvider would have fallen back
            // to anyway. Keeps the column non-null for every tenant going forward, matching the
            // AddTenantTimezone migration's one-time backfill of pre-existing tenants.
            Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? TimeZoneCatalog.DefaultId : request.Timezone,
            ProductName = TenantBusinessDetails.Clean(request.ProductName)
        };
        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync(cancellationToken);

        var user = new ApplicationUser
        {
            TenantId = tenant.Id,
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            IsActive = true,
            // Not confirmed until they follow the emailed link: anyone can type anyone's address into a signup form.
            EmailConfirmed = false,
            CreatedAt = _dateTime.UtcNow
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            throw new ValidationException(result.Errors.Select(e => new FluentValidation.Results.ValidationFailure(nameof(request.Password), e.Description)));

        await _userManager.AddToRoleAsync(user, AppRoles.Admin);

        tenant.OwnerUserId = user.Id;
        await _context.SaveChangesAsync(cancellationToken);

        // Creates this tenant's background job schedules and registers them with Hangfire, so its first
        // campaign can send within the minute rather than waiting for the daily reconcile pass.
        await _jobProvisioner.SyncTenantAsync(tenant.Id, cancellationToken);

        // A trial tenant has no plan and so no included quota - without this it could not send a message before
        // paying. The grant expires with the trial.
        await _quota.GrantTrialAsync(tenant.Id, tenant.TrialEndsAtUtc!.Value, cancellationToken);

        // Gives the new tenant a working AI sales agent from its first message: without a qualification
        // schema the agent has nothing to ask about, and without scoring rules every lead reads Cold.
        // Both calls are additive and idempotent, so a tenant that later configures its own is
        // unaffected, and a retry of this signup cannot double-seed.
        await _qualification.SeedDefaultsAsync(tenant.Id, cancellationToken);
        await _leadScoring.SeedDefaultsAsync(tenant.Id, cancellationToken);

        // The operators run the platform and see nothing of a signup unless told. Never throws, so a mail problem can't fail it.
        await _platformNotifier.NotifyAsync(new PlatformNotificationRequest(
            PlatformNotificationKind.TenantSignedUp, PlatformNotificationSeverity.Info, $"signup-{tenant.Id:N}",
            $"New signup: {tenant.Name}",
            $"{tenant.Name} ({tenant.Slug}) signed up{(tenant.CountryCode is null ? string.Empty : $" from {tenant.CountryCode}")}. " +
            $"Admin: {user.FullName} <{user.Email}>. Their trial ends {tenant.TrialEndsAtUtc:d MMM yyyy}.",
            tenant.Id), cancellationToken);

        await _recovery.SendVerificationEmailAsync(user.Id, cancellationToken);

        var roles = await _userManager.GetRolesAsync(user);
        return await IssueTokenPairAsync(user, roles, ipAddress, cancellationToken);
    }

    public async Task<TokenPairDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        await _loginValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            _userManager.PasswordHasher.VerifyHashedPassword(DecoyUser, DecoyHash, request.Password);
            throw new AuthenticationFailedException();
        }

        // Locked out means no password is tried at all - not even the right one - or the lock would only slow a
        // guesser down, not stop them.
        if (await _userManager.IsLockedOutAsync(user))
            throw new AuthenticationFailedException(LockedOutMessage);

        if (!user.IsActive || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            if (user.IsActive)
            {
                await _userManager.AccessFailedAsync(user);
                if (await _userManager.IsLockedOutAsync(user))
                {
                    await NotifyLockedAsync(user, cancellationToken);
                    throw new AuthenticationFailedException(LockedOutMessage);
                }
            }

            throw new AuthenticationFailedException();
        }

        await _userManager.ResetAccessFailedCountAsync(user);

        var tenant = user.TenantId is { } userTenantId
            ? await _context.Tenants.FirstOrDefaultAsync(t => t.Id == userTenantId, cancellationToken)
            : null;

        if (user.TenantId is not null && (tenant is null || tenant.Status is TenantStatus.Suspended or TenantStatus.Cancelled))
            throw new AuthenticationFailedException("This workspace is not available. Contact support.");

        // UX nicety only, not a security boundary - see LoginRequest.Slug's doc comment.
        if (!string.IsNullOrWhiteSpace(request.Slug) &&
            !string.Equals(tenant?.Slug, request.Slug, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationFailedException("This account does not belong to that workspace.");
        }

        var roles = await _userManager.GetRolesAsync(user);
        var tokenPair = await IssueTokenPairAsync(user, roles, ipAddress, cancellationToken);

        user.LastLoginAt = _dateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        return tokenPair;
    }

    public async Task<TokenPairDto> RefreshTokenAsync(RefreshTokenRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        await _refreshTokenValidator.ValidateAndThrowAsync(request, cancellationToken);

        var hash = _jwtTokenService.HashToken(request.RefreshToken);
        var existingToken = await _context.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existingToken is null)
            throw new AuthenticationFailedException("Invalid refresh token.");

        if (existingToken.RevokedAt is not null)
        {
            // The same refresh token was presented twice - treat as possible theft and kill every
            // active session for this user, forcing a fresh login.
            var activeTokens = await _context.RefreshTokens
                .Where(t => t.UserId == existingToken.UserId && t.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var token in activeTokens)
                token.RevokedAt = _dateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            throw new AuthenticationFailedException("Refresh token has already been used. All sessions have been revoked; please log in again.");
        }

        if (existingToken.ExpiresAt <= _dateTime.UtcNow)
            throw new AuthenticationFailedException("Refresh token has expired.");

        var user = await _userManager.FindByIdAsync(existingToken.UserId.ToString());
        if (user is null || !user.IsActive)
            throw new AuthenticationFailedException();

        var roles = await _userManager.GetRolesAsync(user);
        var newRefreshToken = _jwtTokenService.GenerateRefreshToken();

        existingToken.RevokedAt = _dateTime.UtcNow;
        existingToken.ReplacedByTokenHash = _jwtTokenService.HashToken(newRefreshToken.Token);

        return await IssueTokenPairAsync(user, roles, ipAddress, cancellationToken, newRefreshToken);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = _jwtTokenService.HashToken(refreshToken);
        var token = await _context.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, cancellationToken);
        if (token is null)
            return;

        token.RevokedAt = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        await _changePasswordValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new NotFoundException(nameof(ApplicationUser), userId);

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            throw new ValidationException(result.Errors.Select(e => new FluentValidation.Results.ValidationFailure(nameof(request.NewPassword), e.Description)));

        // Whoever else holds a session - including a thief - is signed out. The caller's own access token keeps
        // working until it expires, and they sign in again with the new password when it does.
        await _recovery.RevokeAllSessionsAsync(userId, cancellationToken);
    }

    /// <summary>Tells the workspace's admins that someone was just locked out - a forgotten password or a guesser, and only a person can
    /// tell which. Once per lock (the episode is the lock's end time). Never throws: the notifier swallows its own delivery problems.</summary>
    private async Task NotifyLockedAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        if (user.TenantId is not { } tenantId)
            return;

        var name = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName;
        await _tenantNotifier.NotifyAsync(new TenantNotificationRequest(
            tenantId, TenantNotificationKind.AccountLocked, null,
            $"lockout-{user.Id:N}-{user.LockoutEnd?.UtcTicks}",
            $"Sign-in locked for {name}",
            $"{name} ({user.Email}) was locked out for 15 minutes after repeated wrong passwords. If it was them, they can reset their password " +
            "from the sign-in page. If it was not, the Audit Log shows each failed attempt and the address it came from.",
            AlsoWhatsApp: false), cancellationToken);
    }

    private async Task<TokenPairDto> IssueTokenPairAsync(
        ApplicationUser user,
        IList<string> roles,
        string? ipAddress,
        CancellationToken cancellationToken,
        JwtTokenResult? refreshTokenResult = null)
    {
        var roleList = roles.ToList();
        var accessToken = _jwtTokenService.GenerateAccessToken(user, roleList);
        var refreshToken = refreshTokenResult ?? _jwtTokenService.GenerateRefreshToken();

        _context.RefreshTokens.Add(new RefreshToken
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            TokenHash = _jwtTokenService.HashToken(refreshToken.Token),
            ExpiresAt = refreshToken.ExpiresAtUtc,
            CreatedByIp = ipAddress
        });

        await _context.SaveChangesAsync(cancellationToken);

        return new TokenPairDto(
            accessToken.Token,
            accessToken.ExpiresAtUtc,
            refreshToken.Token,
            refreshToken.ExpiresAtUtc,
            user.ToDto(roleList));
    }
}
