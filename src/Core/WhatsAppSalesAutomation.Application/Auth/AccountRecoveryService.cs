using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Auth;

/// <summary>Getting back into an account, and proving an email address or phone number is yours.</summary>
public interface IAccountRecoveryService
{
    /// <summary>Sends a reset link (email) or code (SMS) for the account at the given email, if there is one. Says nothing
    /// either way: the caller - and so anyone probing - cannot tell whether an address has an account, or whether it has
    /// a verified phone.</summary>
    Task RequestPasswordResetAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sets a new password from an emailed token or an SMS code. Ends every session the account had and lifts any
    /// lockout - whoever held the old password, or a stolen token, is signed out.</summary>
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>Emails a link to confirm the account's address. Never throws: a mail problem must not fail signup.</summary>
    Task SendVerificationEmailAsync(Guid userId, CancellationToken cancellationToken = default);

    Task VerifyEmailAsync(VerifyEmailRequest request, CancellationToken cancellationToken = default);

    /// <summary>Texts a code to the phone number on the user's profile. Unlike a reset, the caller is signed in, so it
    /// says plainly what is wrong (no number, SMS not set up, asked too soon).</summary>
    Task SendPhoneVerificationCodeAsync(Guid userId, CancellationToken cancellationToken = default);

    Task VerifyPhoneAsync(Guid userId, VerifyPhoneRequest request, CancellationToken cancellationToken = default);

    /// <summary>Revokes every live refresh token of the user, so no existing session can renew itself.
    /// An access token already issued still works until it expires (minutes, not days).</summary>
    Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken = default);
}

public class AccountRecoveryService : IAccountRecoveryService
{
    /// <summary>One message for every way a link or code can be bad - wrong, expired, already used, wrong account - so the
    /// response teaches an attacker nothing.</summary>
    public const string InvalidLinkMessage = "This link or code is invalid or has expired. Request a new one.";

    public const string Email = "email";
    public const string Sms = "sms";

    /// <summary>Identity's TOTP phone provider keys its codes to the purpose, so a reset code cannot confirm a phone.</summary>
    private const string ResetPurpose = "sms-reset-password";
    private const string VerifyPurpose = "sms-verify-phone";

    /// <summary>A code costs real money to send; this is the floor between two for the same account and purpose.</summary>
    private static readonly TimeSpan SmsCooldown = TimeSpan.FromSeconds(60);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _email;
    private readonly ISmsOtpSender _sms;
    private readonly IDateTimeProvider _dateTime;
    private readonly IMemoryCache _cache;
    private readonly IOptionsSnapshot<AppLinkOptions> _links;
    private readonly IValidator<ForgotPasswordRequest> _forgotValidator;
    private readonly IValidator<ResetPasswordRequest> _resetValidator;
    private readonly IValidator<VerifyEmailRequest> _verifyValidator;
    private readonly IValidator<VerifyPhoneRequest> _verifyPhoneValidator;
    private readonly ILogger<AccountRecoveryService> _logger;

    public AccountRecoveryService(
        UserManager<ApplicationUser> userManager,
        IApplicationDbContext context,
        IEmailSender email,
        ISmsOtpSender sms,
        IDateTimeProvider dateTime,
        IMemoryCache cache,
        IOptionsSnapshot<AppLinkOptions> links,
        IValidator<ForgotPasswordRequest> forgotValidator,
        IValidator<ResetPasswordRequest> resetValidator,
        IValidator<VerifyEmailRequest> verifyValidator,
        IValidator<VerifyPhoneRequest> verifyPhoneValidator,
        ILogger<AccountRecoveryService> logger)
    {
        _userManager = userManager;
        _context = context;
        _email = email;
        _sms = sms;
        _dateTime = dateTime;
        _cache = cache;
        _links = links;
        _forgotValidator = forgotValidator;
        _resetValidator = resetValidator;
        _verifyValidator = verifyValidator;
        _verifyPhoneValidator = verifyPhoneValidator;
        _logger = logger;
    }

    public async Task RequestPasswordResetAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        await _forgotValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive)
            return;

        if (IsSms(request.Channel))
        {
            // Only a phone the user has proven is theirs may receive a reset code: otherwise whoever could edit the
            // profile number could redirect it.
            if (!user.PhoneNumberConfirmed || !PhoneNumbers.TryNormalize(user.PhoneNumber, out var phone) || OnCooldown(user.Id, ResetPurpose))
                return;

            var code = await _userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultPhoneProvider, ResetPurpose);
            await SendCodeAsync(phone, code, cancellationToken);
            return;
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var link = BuildLink("reset-password", user.Email!, token);
        if (link is null)
            return;

        await SendEmailAsync(user.Email!, "Reset your password",
            $"Hi {user.FullName},\n\nSomeone asked to reset the password for this account. If it was you, choose a new one here:\n\n{link}\n\n" +
            "The link works once and expires soon. If you did not ask for it, ignore this email - your password has not changed.",
            cancellationToken);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        await _resetValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive)
            throw Invalid(nameof(request.Token));

        string resetToken;
        if (IsSms(request.Channel))
        {
            if (!user.PhoneNumberConfirmed)
                throw Invalid(nameof(request.Token));

            // A six-digit code can be guessed, so wrong tries count against the same lockout a password does: five
            // guesses, then fifteen minutes. A locked-out account resets by email link instead.
            if (await _userManager.IsLockedOutAsync(user))
                throw new ValidationException(new[] { new FluentValidation.Results.ValidationFailure(nameof(request.Token), AuthService.LockedOutMessage) });

            var valid = await _userManager.VerifyUserTokenAsync(user, TokenOptions.DefaultPhoneProvider, ResetPurpose, request.Token.Trim());
            if (!valid)
            {
                await _userManager.AccessFailedAsync(user);
                throw Invalid(nameof(request.Token));
            }

            // The code only proves who is asking; Identity still wants its own reset token to set the password.
            resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
        }
        else
        {
            resetToken = request.Token;
        }

        var result = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
                throw Invalid(nameof(request.Token));

            // A password the policy rejects is the user's to fix, and says so. The code or link is not spent.
            throw new ValidationException(result.Errors.Select(e =>
                new FluentValidation.Results.ValidationFailure(nameof(request.NewPassword), e.Description)));
        }

        // Proof of control of the address (a link) or the number (a code) settles that verification too.
        if (!IsSms(request.Channel) && !user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await _userManager.UpdateAsync(user);
        }

        await _userManager.SetLockoutEndDateAsync(user, null);
        await _userManager.ResetAccessFailedCountAsync(user);
        await RevokeAllSessionsAsync(user.Id, cancellationToken);
    }

    public async Task SendVerificationEmailAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user is null || user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email))
                return;

            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var link = BuildLink("verify-email", user.Email, token);
            if (link is null)
                return;

            await SendEmailAsync(user.Email, "Confirm your email address",
                $"Hi {user.FullName},\n\nPlease confirm this is your email address:\n\n{link}\n\n" +
                "If you did not create an account, ignore this email.",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not send a verification email to user {UserId}", userId);
        }
    }

    public async Task VerifyEmailAsync(VerifyEmailRequest request, CancellationToken cancellationToken = default)
    {
        await _verifyValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
            throw Invalid(nameof(request.Token));

        if (user.EmailConfirmed)
            return;

        var result = await _userManager.ConfirmEmailAsync(user, request.Token);
        if (!result.Succeeded)
            throw Invalid(nameof(request.Token));
    }

    public async Task SendPhoneVerificationCodeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await FindActiveAsync(userId);

        if (user.PhoneNumberConfirmed)
            throw Problem("PhoneNumber", "This phone number is already verified.");

        if (!PhoneNumbers.TryNormalize(user.PhoneNumber, out var phone))
            throw Problem("PhoneNumber", "Add your phone number with the country code (for example +919876543210) to your profile first.");

        if (OnCooldown(user.Id, VerifyPurpose))
            throw Problem("PhoneNumber", "A code was just sent. Wait a minute before asking for another.");

        var code = await _userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultPhoneProvider, VerifyPurpose + ":" + phone);
        var result = await _sms.SendOtpAsync(phone, code, cancellationToken);

        if (result.Skipped)
            throw Problem("PhoneNumber", "Text message verification is not set up on this platform yet.");
        if (!result.Success)
        {
            _logger.LogWarning("SMS code to user {UserId} was not sent: {Reason}", userId, result.Note);
            throw Problem("PhoneNumber", "We could not send the text message. Try again in a moment.");
        }
    }

    public async Task VerifyPhoneAsync(Guid userId, VerifyPhoneRequest request, CancellationToken cancellationToken = default)
    {
        await _verifyPhoneValidator.ValidateAndThrowAsync(request, cancellationToken);
        var user = await FindActiveAsync(userId);

        if (user.PhoneNumberConfirmed)
            return;

        if (!PhoneNumbers.TryNormalize(user.PhoneNumber, out var phone))
            throw Problem("Code", InvalidLinkMessage);

        if (await _userManager.IsLockedOutAsync(user))
            throw Problem("Code", AuthService.LockedOutMessage);

        // Keyed to the number as well as the purpose, so a code sent to one number cannot confirm another.
        var valid = await _userManager.VerifyUserTokenAsync(user, TokenOptions.DefaultPhoneProvider, VerifyPurpose + ":" + phone, request.Code.Trim());
        if (!valid)
        {
            await _userManager.AccessFailedAsync(user);
            throw Problem("Code", InvalidLinkMessage);
        }

        user.PhoneNumberConfirmed = true;
        await _userManager.UpdateAsync(user);
    }

    public async Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var live = await _context.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (live.Count == 0)
            return;

        foreach (var token in live)
            token.RevokedAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<ApplicationUser> FindActiveAsync(Guid userId) =>
        await _userManager.FindByIdAsync(userId.ToString()) is { IsActive: true } user
            ? user
            : throw Problem("User", "This account is not available.");

    private static bool IsSms(string? channel) => string.Equals(channel, Sms, StringComparison.OrdinalIgnoreCase);

    private static ValidationException Invalid(string property) => Problem(property, InvalidLinkMessage);

    private static ValidationException Problem(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });

    /// <summary>True (and starts the clock) when a code for this account and purpose went out within the cooldown.</summary>
    private bool OnCooldown(Guid userId, string purpose)
    {
        var key = $"otp-cooldown:{purpose}:{userId}";
        if (_cache.TryGetValue(key, out _))
            return true;

        _cache.Set(key, true, SmsCooldown);
        return false;
    }

    /// <summary>The web app link, or null (and a warning) when no public address is configured.</summary>
    private string? BuildLink(string path, string email, string token)
    {
        var baseUrl = _links.Value.PublicUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            _logger.LogWarning("Not sending the {Path} email: App:PublicUrl is not configured, and a link must never be built from the request.", path);
            return null;
        }

        return $"{baseUrl}/{path}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }

    private async Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        var result = await _email.SendAsync(to, subject, body, cancellationToken);
        if (!result.Success)
            _logger.LogWarning("Email '{Subject}' to {To} was not sent: {Reason}", subject, to, result.Note);
    }

    private async Task SendCodeAsync(string phone, string code, CancellationToken cancellationToken)
    {
        var result = await _sms.SendOtpAsync(phone, code, cancellationToken);
        if (!result.Success)
            _logger.LogWarning("SMS reset code was not sent: {Reason}", result.Note);
    }
}
