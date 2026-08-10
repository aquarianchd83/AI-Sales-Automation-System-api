namespace WhatsAppSalesAutomation.Application.Auth;

public interface IAuthService
{
    Task<TokenPairDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<TokenPairDto> RefreshTokenAsync(RefreshTokenRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default);
}
