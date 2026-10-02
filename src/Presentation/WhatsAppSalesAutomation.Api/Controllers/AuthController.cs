using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Application.Auth;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Authorize]
// The whole controller, not just the anonymous actions: change-password is as much a credential
// endpoint as login is, and an attacker with a stolen token guessing a current password is exactly
// the case a per-IP budget should cover.
[EnableRateLimiting(RateLimitingServiceExtensions.AuthPolicy)]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IAccountRecoveryService _recovery;

    public AuthController(IAuthService authService, IAccountRecoveryService recovery)
    {
        _authService = authService;
        _recovery = recovery;
    }

    [HttpPost("signup")]
    [AllowAnonymous]
    public async Task<ActionResult<TokenPairDto>> SignUp([FromBody] TenantSignUpRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.SignUpAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return Ok(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<TokenPairDto>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return Ok(result);
    }

    [HttpPost("refresh-token")]
    [AllowAnonymous]
    public async Task<ActionResult<TokenPairDto>> RefreshToken([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await _authService.RefreshTokenAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return Ok(result);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        await _authService.LogoutAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _authService.ChangePasswordAsync(userId, request, cancellationToken);
        return NoContent();
    }

    /// <summary>Always 204, whether or not the address has an account (or a verified phone, for the sms channel) - the
    /// response must not tell a stranger which addresses are registered.</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await _recovery.RequestPasswordResetAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await _recovery.ResetPasswordAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("verify-email")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        await _recovery.VerifyEmailAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(CancellationToken cancellationToken)
    {
        await _recovery.SendVerificationEmailAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), cancellationToken);
        return NoContent();
    }

    /// <summary>Texts a code to the phone number on the signed-in user's profile.</summary>
    [HttpPost("phone/send-code")]
    public async Task<IActionResult> SendPhoneCode(CancellationToken cancellationToken)
    {
        await _recovery.SendPhoneVerificationCodeAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), cancellationToken);
        return NoContent();
    }

    [HttpPost("phone/verify")]
    public async Task<IActionResult> VerifyPhone([FromBody] VerifyPhoneRequest request, CancellationToken cancellationToken)
    {
        await _recovery.VerifyPhoneAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), request, cancellationToken);
        return NoContent();
    }
}
