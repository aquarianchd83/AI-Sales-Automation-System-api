using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Users;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Auth;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IApplicationDbContext _context;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IDateTimeProvider _dateTime;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IValidator<RefreshTokenRequest> _refreshTokenValidator;
    private readonly IValidator<ChangePasswordRequest> _changePasswordValidator;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        IApplicationDbContext context,
        IJwtTokenService jwtTokenService,
        IDateTimeProvider dateTime,
        IValidator<LoginRequest> loginValidator,
        IValidator<RefreshTokenRequest> refreshTokenValidator,
        IValidator<ChangePasswordRequest> changePasswordValidator)
    {
        _userManager = userManager;
        _context = context;
        _jwtTokenService = jwtTokenService;
        _dateTime = dateTime;
        _loginValidator = loginValidator;
        _refreshTokenValidator = refreshTokenValidator;
        _changePasswordValidator = changePasswordValidator;
    }

    public async Task<TokenPairDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        await _loginValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive || !await _userManager.CheckPasswordAsync(user, request.Password))
            throw new AuthenticationFailedException();

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
