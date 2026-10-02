using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Auth;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Getting back into an account by email link or SMS code, and proving an email or phone is yours.</summary>
public sealed class AccountRecoveryTests : IDisposable
{
    private const string NewPassword = "New-Passw0rd!";
    private const string Phone = "+919876543210";

    private readonly IdentityHarness _h = new();
    private readonly CapturingEmail _email = new();
    private readonly CapturingSms _sms = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public void Dispose() => _h.Dispose();

    private AccountRecoveryService Recovery(string? publicUrl = "https://app.test/") => new(
        _h.Users, _h.Db, _email, _sms, _h.Clock, _cache,
        new FixedOptions<AppLinkOptions>(new AppLinkOptions { PublicUrl = publicUrl ?? string.Empty }),
        new ForgotPasswordRequestValidator(), new ResetPasswordRequestValidator(), new VerifyEmailRequestValidator(), new VerifyPhoneRequestValidator(),
        NullLogger<AccountRecoveryService>.Instance);

    private static string TokenFrom(string body) => Uri.UnescapeDataString(Regex.Match(body, @"token=([^\s&]+)").Groups[1].Value);

    private async Task<ApplicationUser> ReloadAsync(ApplicationUser user) => (await _h.Users.FindByIdAsync(user.Id.ToString()))!;

    private async Task AddSessionAsync(Guid userId, string hash)
    {
        _h.Db.RefreshTokens.Add(new RefreshToken { UserId = userId, TokenHash = hash, ExpiresAt = DateTime.UtcNow.AddDays(7) });
        await _h.Db.SaveChangesAsync();
    }

    // ── Reset by email link ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reset_email_links_to_the_configured_address_with_the_email_and_token()
    {
        await _h.AddUserAsync();

        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com"));

        var mail = Assert.Single(_email.Sent);
        Assert.Equal("asha@example.com", mail.To);
        Assert.Contains("https://app.test/reset-password?email=asha%40example.com&token=", mail.Body);
    }

    [Fact]
    public async Task Asking_for_an_unknown_address_says_nothing_and_sends_nothing()
    {
        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("nobody@example.com"));

        Assert.Empty(_email.Sent);
        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task With_no_public_address_configured_no_link_is_built_from_anywhere_else()
    {
        await _h.AddUserAsync();

        await Recovery(publicUrl: null).RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com"));

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task The_emailed_token_sets_a_new_password_ends_every_session_and_lifts_a_lockout()
    {
        var user = await _h.AddUserAsync();
        await AddSessionAsync(user.Id, "s1");
        await AddSessionAsync(user.Id, "s2");
        for (var i = 0; i < 5; i++)
            await _h.Users.AccessFailedAsync(user);
        Assert.True(await _h.Users.IsLockedOutAsync(user));
        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com"));

        await Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", TokenFrom(_email.Sent[0].Body), NewPassword));

        user = await ReloadAsync(user);
        Assert.True(await _h.Users.CheckPasswordAsync(user, NewPassword));
        Assert.False(await _h.Users.CheckPasswordAsync(user, IdentityHarness.Password));
        Assert.False(await _h.Users.IsLockedOutAsync(user));
        Assert.All(await _h.Db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task A_reset_link_works_once()
    {
        await _h.AddUserAsync();
        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com"));
        var token = TokenFrom(_email.Sent[0].Body);
        await Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", token, NewPassword));

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", token, "Another-Passw0rd!")));

        Assert.Contains(AccountRecoveryService.InvalidLinkMessage, error.Message);
    }

    [Fact]
    public async Task A_wrong_token_and_an_unknown_account_get_the_same_answer()
    {
        await _h.AddUserAsync();

        var wrongToken = await Assert.ThrowsAsync<ValidationException>(() =>
            Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", "not-a-token", NewPassword)));
        var noAccount = await Assert.ThrowsAsync<ValidationException>(() =>
            Recovery().ResetPasswordAsync(new ResetPasswordRequest("nobody@example.com", "not-a-token", NewPassword)));

        Assert.Equal(wrongToken.Message, noAccount.Message);
    }

    [Fact]
    public async Task A_password_the_policy_rejects_is_explained_and_the_link_is_not_spent()
    {
        await _h.AddUserAsync();
        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com"));
        var token = TokenFrom(_email.Sent[0].Body);

        await Assert.ThrowsAsync<ValidationException>(() =>
            Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", token, "weakweak1!")));
        await Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", token, NewPassword));

        Assert.True(await _h.Users.CheckPasswordAsync((await _h.Users.FindByEmailAsync("asha@example.com"))!, NewPassword));
    }

    // ── Reset by SMS code ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_phone_that_was_never_verified_is_sent_no_reset_code()
    {
        await _h.AddUserAsync(phone: Phone, phoneConfirmed: false);

        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com", "sms"));

        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task A_verified_phone_gets_a_six_digit_code_that_resets_the_password()
    {
        var user = await _h.AddUserAsync(phone: Phone, phoneConfirmed: true);
        await AddSessionAsync(user.Id, "s1");

        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com", "sms"));
        var sent = Assert.Single(_sms.Sent);
        Assert.Equal(Phone, sent.Phone);
        Assert.Matches(@"^\d{6}$", sent.Code);

        await Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", sent.Code, NewPassword, "sms"));

        Assert.True(await _h.Users.CheckPasswordAsync(await ReloadAsync(user), NewPassword));
        Assert.All(await _h.Db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task A_second_code_within_a_minute_is_not_sent()
    {
        await _h.AddUserAsync(phone: Phone, phoneConfirmed: true);

        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com", "sms"));
        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com", "sms"));

        Assert.Single(_sms.Sent);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_so_the_right_one_no_longer_works()
    {
        var user = await _h.AddUserAsync(phone: Phone, phoneConfirmed: true);
        await Recovery().RequestPasswordResetAsync(new ForgotPasswordRequest("asha@example.com", "sms"));
        var code = _sms.Sent[0].Code;
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<ValidationException>(() =>
                Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", wrong, NewPassword, "sms")));

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", code, NewPassword, "sms")));

        Assert.Contains(AuthService.LockedOutMessage, error.Message);
        Assert.False(await _h.Users.CheckPasswordAsync(await ReloadAsync(user), NewPassword));
    }

    [Fact]
    public async Task A_code_cannot_reset_a_password_for_an_unverified_phone()
    {
        await _h.AddUserAsync(phone: Phone, phoneConfirmed: false);

        await Assert.ThrowsAsync<ValidationException>(() =>
            Recovery().ResetPasswordAsync(new ResetPasswordRequest("asha@example.com", "123456", NewPassword, "sms")));
    }

    // ── Verifying a phone ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_code_texted_to_the_profile_number_verifies_it()
    {
        var user = await _h.AddUserAsync(phone: "+91 98765-43210");

        await Recovery().SendPhoneVerificationCodeAsync(user.Id);
        Assert.Equal(Phone, Assert.Single(_sms.Sent).Phone);
        await Recovery().VerifyPhoneAsync(user.Id, new VerifyPhoneRequest(_sms.Sent[0].Code));

        Assert.True((await ReloadAsync(user)).PhoneNumberConfirmed);
    }

    [Fact]
    public async Task A_wrong_code_does_not_verify_and_counts_as_a_failed_attempt()
    {
        var user = await _h.AddUserAsync(phone: Phone);
        await Recovery().SendPhoneVerificationCodeAsync(user.Id);
        var wrong = _sms.Sent[0].Code == "000000" ? "111111" : "000000";

        await Assert.ThrowsAsync<ValidationException>(() => Recovery().VerifyPhoneAsync(user.Id, new VerifyPhoneRequest(wrong)));

        user = await ReloadAsync(user);
        Assert.False(user.PhoneNumberConfirmed);
        Assert.Equal(1, user.AccessFailedCount);
    }

    [Fact]
    public async Task A_code_sent_to_one_number_cannot_verify_another()
    {
        var user = await _h.AddUserAsync(phone: Phone);
        await Recovery().SendPhoneVerificationCodeAsync(user.Id);

        user.PhoneNumber = "+919000000000";
        await _h.Users.UpdateAsync(user);

        await Assert.ThrowsAsync<ValidationException>(() => Recovery().VerifyPhoneAsync(user.Id, new VerifyPhoneRequest(_sms.Sent[0].Code)));
    }

    [Fact]
    public async Task Asking_to_verify_without_a_usable_number_says_what_to_fix()
    {
        var none = await _h.AddUserAsync("none@example.com");
        var local = await _h.AddUserAsync("local@example.com", phone: "98765 43210");

        var noNumber = await Assert.ThrowsAsync<ValidationException>(() => Recovery().SendPhoneVerificationCodeAsync(none.Id));
        var noCountryCode = await Assert.ThrowsAsync<ValidationException>(() => Recovery().SendPhoneVerificationCodeAsync(local.Id));

        Assert.Contains("country code", noNumber.Message);
        Assert.Contains("country code", noCountryCode.Message);
        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task When_sms_is_not_set_up_the_user_is_told_so()
    {
        var user = await _h.AddUserAsync(phone: Phone);
        _sms.Result = new DeliveryResult(false, "SMS is not set up", Skipped: true);

        var error = await Assert.ThrowsAsync<ValidationException>(() => Recovery().SendPhoneVerificationCodeAsync(user.Id));

        Assert.Contains("not set up", error.Message);
    }

    // ── Verifying an email ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_emailed_link_confirms_the_address_and_a_bad_token_does_not()
    {
        var user = await _h.AddUserAsync(emailConfirmed: false);

        await Recovery().SendVerificationEmailAsync(user.Id);
        var mail = Assert.Single(_email.Sent);
        Assert.Contains("https://app.test/verify-email?email=", mail.Body);

        await Assert.ThrowsAsync<ValidationException>(() => Recovery().VerifyEmailAsync(new VerifyEmailRequest("asha@example.com", "nope")));
        Assert.False((await ReloadAsync(user)).EmailConfirmed);

        await Recovery().VerifyEmailAsync(new VerifyEmailRequest("asha@example.com", TokenFrom(mail.Body)));
        Assert.True((await ReloadAsync(user)).EmailConfirmed);
    }

    [Fact]
    public async Task An_already_confirmed_address_is_not_emailed_again()
    {
        var user = await _h.AddUserAsync();

        await Recovery().SendVerificationEmailAsync(user.Id);

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task A_mail_failure_never_fails_the_caller()
    {
        var user = await _h.AddUserAsync(emailConfirmed: false);
        _email.Throw = true;

        await Recovery().SendVerificationEmailAsync(user.Id);
    }

    [Theory]
    [InlineData("+91 98765-43210", true, "+919876543210")]
    [InlineData("+1 (415) 555-0100", true, "+14155550100")]
    [InlineData("98765 43210", false, "")]
    [InlineData("+12", false, "")]
    [InlineData("", false, "")]
    public void Phone_numbers_need_the_country_code_to_be_usable(string raw, bool ok, string expected)
    {
        Assert.Equal(ok, PhoneNumbers.TryNormalize(raw, out var e164));
        Assert.Equal(expected, e164);
    }

    private sealed class CapturingEmail : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();
        public bool Throw { get; set; }

        public Task<DeliveryResult> SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
        {
            if (Throw)
                throw new InvalidOperationException("smtp down");

            Sent.Add((toEmail, subject, body));
            return Task.FromResult(new DeliveryResult(true));
        }
    }

    private sealed class CapturingSms : ISmsOtpSender
    {
        public List<(string Phone, string Code)> Sent { get; } = new();
        public DeliveryResult Result { get; set; } = new(true);

        public Task<DeliveryResult> SendOtpAsync(string toPhoneE164, string code, CancellationToken cancellationToken = default)
        {
            if (Result.Success)
                Sent.Add((toPhoneE164, code));
            return Task.FromResult(Result);
        }
    }
}
