using WhatsAppSalesAutomation.Application.Users;

namespace WhatsAppSalesAutomation.Application.Auth;

public record LoginRequest(string Email, string Password);

public record RefreshTokenRequest(string RefreshToken);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record TokenPairDto(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserDto User);
