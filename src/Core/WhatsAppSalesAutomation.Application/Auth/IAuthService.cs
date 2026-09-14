namespace WhatsAppSalesAutomation.Application.Auth;

public interface IAuthService
{
    /// <summary>Creates a new Tenant (on a 14-day trial) plus its first Admin user, then logs that
    /// user in - the only self-serve account-creation path in the app (every other user is created by
    /// an existing tenant Admin/SuperAdmin via <c>IUserService.CreateAsync</c>).</summary>
    Task<TokenPairDto> SignUpAsync(TenantSignUpRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<TokenPairDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<TokenPairDto> RefreshTokenAsync(RefreshTokenRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default);
}
